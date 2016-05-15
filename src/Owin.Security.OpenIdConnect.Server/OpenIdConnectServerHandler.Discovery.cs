/*
 * Licensed under the Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0)
 * See https://github.com/aspnet-contrib/AspNet.Security.OpenIdConnect.Server
 * for more information concerning the license and the contributors participating to this project.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IdentityModel.Tokens;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.Owin.Security.Infrastructure;
using Newtonsoft.Json.Linq;
using Owin.Security.OpenIdConnect.Extensions;

namespace Owin.Security.OpenIdConnect.Server {
    internal partial class OpenIdConnectServerHandler : AuthenticationHandler<OpenIdConnectServerOptions> {
        private async Task<bool> InvokeConfigurationEndpointAsync() {
            // Metadata requests must be made via GET.
            // See http://openid.net/specs/openid-connect-discovery-1_0.html#ProviderConfigurationRequest
            if (!string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase)) {
                Options.Logger.LogError("The discovery request was rejected because an invalid " +
                                        "HTTP method was used: {Method}.", Request.Method);

                return await SendConfigurationResponseAsync(null, new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "Invalid HTTP method: make sure to use GET."
                });
            }

            var request = new OpenIdConnectMessage(Request.Query);

            var context = new ValidateConfigurationRequestContext(Context, Options);
            await Options.Provider.ValidateConfigurationRequest(context);

            // Stop processing the request if Validated was not called.
            if (!context.IsValidated) {
                Options.Logger.LogError("The discovery request was rejected with the following error: {Error} ; {Description}",
                                        /* Error: */ context.Error ?? OpenIdConnectConstants.Errors.InvalidRequest,
                                        /* Description: */ context.ErrorDescription);

                return await SendConfigurationResponseAsync(request, new OpenIdConnectMessage {
                    Error = context.Error ?? OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = context.ErrorDescription,
                    ErrorUri = context.ErrorUri
                });
            }

            var notification = new HandleConfigurationRequestContext(Context, Options, request);
            notification.Issuer = Context.GetIssuer(Options);

            if (Options.AuthorizationEndpointPath.HasValue) {
                notification.AuthorizationEndpoint = notification.Issuer.AddPath(Options.AuthorizationEndpointPath);
            }

            if (Options.CryptographyEndpointPath.HasValue) {
                notification.CryptographyEndpoint = notification.Issuer.AddPath(Options.CryptographyEndpointPath);
            }

            if (Options.IntrospectionEndpointPath.HasValue) {
                notification.IntrospectionEndpoint = notification.Issuer.AddPath(Options.IntrospectionEndpointPath);
            }

            if (Options.LogoutEndpointPath.HasValue) {
                notification.LogoutEndpoint = notification.Issuer.AddPath(Options.LogoutEndpointPath);
            }

            if (Options.RevocationEndpointPath.HasValue) {
                notification.RevocationEndpoint = notification.Issuer.AddPath(Options.RevocationEndpointPath);
            }

            if (Options.TokenEndpointPath.HasValue) {
                notification.TokenEndpoint = notification.Issuer.AddPath(Options.TokenEndpointPath);
            }

            if (Options.UserinfoEndpointPath.HasValue) {
                notification.UserinfoEndpoint = notification.Issuer.AddPath(Options.UserinfoEndpointPath);
            }

            if (Options.AuthorizationEndpointPath.HasValue) {
                // Only expose the implicit grant type if the token
                // endpoint has not been explicitly disabled.
                notification.GrantTypes.Add(OpenIdConnectConstants.GrantTypes.Implicit);

                if (Options.TokenEndpointPath.HasValue) {
                    // Only expose the authorization code and refresh token grant types
                    // if both the authorization and the token endpoints are enabled.
                    notification.GrantTypes.Add(OpenIdConnectConstants.GrantTypes.AuthorizationCode);
                }
            }

            if (Options.TokenEndpointPath.HasValue) {
                notification.GrantTypes.Add(OpenIdConnectConstants.GrantTypes.RefreshToken);

                // If the authorization endpoint is disabled, assume the authorization server will
                // allow the client credentials and resource owner password credentials grant types.
                if (!Options.AuthorizationEndpointPath.HasValue) {
                    notification.GrantTypes.Add(OpenIdConnectConstants.GrantTypes.ClientCredentials);
                    notification.GrantTypes.Add(OpenIdConnectConstants.GrantTypes.Password);
                }
            }

            // Only populate response_modes_supported and response_types_supported
            // if the authorization endpoint is available.
            if (Options.AuthorizationEndpointPath.HasValue) {
                notification.ResponseModes.Add(OpenIdConnectConstants.ResponseModes.FormPost);
                notification.ResponseModes.Add(OpenIdConnectConstants.ResponseModes.Fragment);
                notification.ResponseModes.Add(OpenIdConnectConstants.ResponseModes.Query);

                notification.ResponseTypes.Add(OpenIdConnectConstants.ResponseTypes.Token);
                notification.ResponseTypes.Add(OpenIdConnectConstants.ResponseTypes.IdToken);

                notification.ResponseTypes.Add(
                    OpenIdConnectConstants.ResponseTypes.IdToken + ' ' +
                    OpenIdConnectConstants.ResponseTypes.Token);

                // Only expose response types containing code when
                // the token endpoint has not been explicitly disabled.
                if (Options.TokenEndpointPath.HasValue) {
                    notification.ResponseTypes.Add(OpenIdConnectConstants.ResponseTypes.Code);

                    notification.ResponseTypes.Add(
                        OpenIdConnectConstants.ResponseTypes.Code + ' ' +
                        OpenIdConnectConstants.ResponseTypes.Token);

                    notification.ResponseTypes.Add(
                        OpenIdConnectConstants.ResponseTypes.Code + ' ' +
                        OpenIdConnectConstants.ResponseTypes.IdToken);

                    notification.ResponseTypes.Add(
                        OpenIdConnectConstants.ResponseTypes.Code + ' ' +
                        OpenIdConnectConstants.ResponseTypes.IdToken + ' ' +
                        OpenIdConnectConstants.ResponseTypes.Token);
                }
            }

            notification.Scopes.Add(OpenIdConnectConstants.Scopes.OpenId);

            notification.SubjectTypes.Add(OpenIdConnectConstants.SubjectTypes.Public);

            notification.SigningAlgorithms.Add(OpenIdConnectConstants.Algorithms.RsaSha256);

            await Options.Provider.HandleConfigurationRequest(notification);

            if (notification.HandledResponse) {
                return true;
            }

            else if (notification.Skipped) {
                return false;
            }
            
            var response = new JObject();

            response.Add(OpenIdConnectConstants.Metadata.Issuer, notification.Issuer);

            if (!string.IsNullOrEmpty(notification.AuthorizationEndpoint)) {
                response.Add(OpenIdConnectConstants.Metadata.AuthorizationEndpoint, notification.AuthorizationEndpoint);
            }

            if (!string.IsNullOrEmpty(notification.CryptographyEndpoint)) {
                response.Add(OpenIdConnectConstants.Metadata.JwksUri, notification.CryptographyEndpoint);
            }

            if (!string.IsNullOrEmpty(notification.IntrospectionEndpoint)) {
                response.Add(OpenIdConnectConstants.Metadata.IntrospectionEndpoint, notification.IntrospectionEndpoint);
            }

            if (!string.IsNullOrEmpty(notification.LogoutEndpoint)) {
                response.Add(OpenIdConnectConstants.Metadata.EndSessionEndpoint, notification.LogoutEndpoint);
            }

            if (!string.IsNullOrEmpty(notification.RevocationEndpoint)) {
                response.Add(OpenIdConnectConstants.Metadata.RevocationEndpoint, notification.RevocationEndpoint);
            }

            if (!string.IsNullOrEmpty(notification.TokenEndpoint)) {
                response.Add(OpenIdConnectConstants.Metadata.TokenEndpoint, notification.TokenEndpoint);
            }

            if (!string.IsNullOrEmpty(notification.UserinfoEndpoint)) {
                response.Add(OpenIdConnectConstants.Metadata.UserinfoEndpoint, notification.UserinfoEndpoint);
            }

            response.Add(OpenIdConnectConstants.Metadata.GrantTypesSupported,
                JArray.FromObject(notification.GrantTypes.Distinct()));

            response.Add(OpenIdConnectConstants.Metadata.ResponseModesSupported,
                JArray.FromObject(notification.ResponseModes.Distinct()));

            response.Add(OpenIdConnectConstants.Metadata.ResponseTypesSupported,
                JArray.FromObject(notification.ResponseTypes.Distinct()));

            response.Add(OpenIdConnectConstants.Metadata.SubjectTypesSupported,
                JArray.FromObject(notification.SubjectTypes.Distinct()));

            response.Add(OpenIdConnectConstants.Metadata.ScopesSupported,
                JArray.FromObject(notification.Scopes.Distinct()));

            response.Add(OpenIdConnectConstants.Metadata.IdTokenSigningAlgValuesSupported,
                JArray.FromObject(notification.SigningAlgorithms.Distinct()));

            return await SendConfigurationResponseAsync(request, response);
        }

        private async Task<bool> InvokeCryptographyEndpointAsync() {
            // Metadata requests must be made via GET.
            // See http://openid.net/specs/openid-connect-discovery-1_0.html#ProviderConfigurationRequest
            if (!string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase)) {
                Options.Logger.LogError("The discovery request was rejected because an invalid " +
                                        "HTTP method was used: {Method}.", Request.Method);

                return await SendCryptographyResponseAsync(null, new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "Invalid HTTP method: make sure to use GET."
                });
            }

            var request = new OpenIdConnectMessage(Request.Query);

            var context = new ValidateCryptographyRequestContext(Context, Options);
            await Options.Provider.ValidateCryptographyRequest(context);

            // Stop processing the request if Validated was not called.
            if (!context.IsValidated) {
                Options.Logger.LogError("The discovery request was rejected with the following error: {Error} ; {Description}",
                                        /* Error: */ context.Error ?? OpenIdConnectConstants.Errors.InvalidRequest,
                                        /* Description: */ context.ErrorDescription);

                return await SendCryptographyResponseAsync(request, new OpenIdConnectMessage {
                    Error = context.Error ?? OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = context.ErrorDescription,
                    ErrorUri = context.ErrorUri
                });
            }

            var notification = new HandleCryptographyRequestContext(Context, Options, request);

            foreach (var credentials in Options.EncryptingCredentials) {
                // Ignore the key if it's not supported.
                if (!(credentials.SecurityKey is AsymmetricSecurityKey) ||
                    (!credentials.SecurityKey.IsSupportedAlgorithm(SecurityAlgorithms.RsaOaepKeyWrap) &&
                     !credentials.SecurityKey.IsSupportedAlgorithm(SecurityAlgorithms.RsaV15KeyWrap))) {
                    Options.Logger.LogInformation("An unsupported encryption key was ignored and excluded " +
                                                  "from the key set: {Type}. Only asymmetric security keys " +
                                                  "supporting RSA1_5 or RSA-OAEP can be exposed via the JWKS " +
                                                  "endpoint.", credentials.SecurityKey.GetType().Name);

                    continue;
                }

                // Try to extract a key identifier from the credentials.
                LocalIdKeyIdentifierClause identifier = null;
                credentials.SecurityKeyIdentifier?.TryFind(out identifier);

                // Resolve the underlying algorithm from the security key.
                var algorithm = (RSA) ((AsymmetricSecurityKey) credentials.SecurityKey)
                    .GetAsymmetricAlgorithm(
                        algorithm: SecurityAlgorithms.RsaOaepKeyWrap,
                        privateKey: false);

                // Skip the key if a RSA instance cannot be retrieved.
                if (algorithm == null) {
                    Options.Logger.LogError("An encryption key was ignored because it was unable " +
                                            "to provide the requested RSA instance.");

                    continue;
                }

                // Export the RSA public key to create a new JSON Web Key
                // exposing the exponent and the modulus parameters.
                var parameters = algorithm.ExportParameters(includePrivateParameters: false);
                Debug.Assert(parameters.Exponent != null, "A null exponent was returned by RSA.ExportParameters()");
                Debug.Assert(parameters.Modulus != null, "A null modulus was returned by RSA.ExportParameters()");

                var key = new JsonWebKey {
                    Use = JsonWebKeyUseNames.Enc,
                    Kty = JsonWebAlgorithmsKeyTypes.RSA,

                    // Resolve the JWA identifier from the algorithm specified in the credentials.
                    Alg = OpenIdConnectServerHelpers.GetJwtAlgorithm(credentials.Algorithm),

                    // Use the key identifier specified
                    // in the signing credentials.
                    Kid = identifier.LocalId,

                    // Both E and N must be base64url-encoded.
                    // See http://tools.ietf.org/html/draft-ietf-jose-json-web-key-31#appendix-A.1
                    E = Base64UrlEncoder.Encode(parameters.Exponent),
                    N = Base64UrlEncoder.Encode(parameters.Modulus)
                };

                X509Certificate2 x509Certificate = null;

                // Determine whether the encrypting credentials are directly based on a X.509 certificate.
                var x509EncryptingCredentials = credentials as X509EncryptingCredentials;
                if (x509EncryptingCredentials != null) {
                    x509Certificate = x509EncryptingCredentials.Certificate;
                }

                // Skip looking for a X509SecurityKey in EncryptingCredentials.SecurityKey
                // if a certificate has been found in the EncryptingCredentials instance.
                if (x509Certificate == null) {
                    // Determine whether the security key is an asymmetric key embedded in a X.509 certificate.
                    var x509SecurityKey = credentials.SecurityKey as X509SecurityKey;
                    if (x509SecurityKey != null) {
                        x509Certificate = x509SecurityKey.Certificate;
                    }
                }

                // Skip looking for a X509AsymmetricSecurityKey in EncryptingCredentials.SecurityKey
                // if a certificate has been found in EncryptingCredentials or EncryptingCredentials.SecurityKey.
                if (x509Certificate == null) {
                    // Determine whether the security key is an asymmetric key embedded in a X.509 certificate.
                    var x509AsymmetricSecurityKey = credentials.SecurityKey as X509AsymmetricSecurityKey;
                    if (x509AsymmetricSecurityKey != null) {
                        // The X.509 certificate is not directly accessible when using X509AsymmetricSecurityKey.
                        // Reflection is the only way to get the certificate used to create the security key.
                        var field = typeof(X509AsymmetricSecurityKey).GetField(
                            name: "certificate",
                            bindingAttr: BindingFlags.Instance | BindingFlags.NonPublic);
                        Debug.Assert(field != null);

                        x509Certificate = (X509Certificate2) field.GetValue(x509AsymmetricSecurityKey);
                    }
                }

                // If the encryption key is embedded in a X.509 certificate, set
                // the x5t and x5c parameters using the certificate details.
                if (x509Certificate != null) {
                    // x5t must be base64url-encoded.
                    // See http://tools.ietf.org/html/draft-ietf-jose-json-web-key-31#section-4.8
                    key.X5t = Base64UrlEncoder.Encode(x509Certificate.GetCertHash());

                    // Unlike E or N, the certificates contained in x5c
                    // must be base64-encoded and not base64url-encoded.
                    // See http://tools.ietf.org/html/draft-ietf-jose-json-web-key-31#section-4.7
                    key.X5c.Add(Convert.ToBase64String(x509Certificate.RawData));
                }

                notification.Keys.Add(key);
            }

            foreach (var credentials in Options.SigningCredentials) {
                // Ignore the key if it's not supported.
                if (!(credentials.SigningKey is AsymmetricSecurityKey) ||
                     !credentials.SigningKey.IsSupportedAlgorithm(SecurityAlgorithms.RsaSha256Signature)) {
                    Options.Logger.LogInformation("An unsupported signing key was ignored and excluded " +
                                                  "from the key set: {Type}. Only asymmetric security keys " +
                                                  "supporting RS256, RS384 or RS512 can be exposed " +
                                                  "via the JWKS endpoint.", credentials.SigningKey.GetType().Name);

                    continue;
                }

                // Try to extract a key identifier from the credentials.
                LocalIdKeyIdentifierClause identifier = null;
                credentials.SigningKeyIdentifier?.TryFind(out identifier);

                // Resolve the underlying algorithm from the security key.
                var algorithm = (RSA) ((AsymmetricSecurityKey) credentials.SigningKey)
                    .GetAsymmetricAlgorithm(
                        algorithm: SecurityAlgorithms.RsaOaepKeyWrap,
                        privateKey: false);

                // Skip the key if a RSA instance cannot be retrieved.
                if (algorithm == null) {
                    Options.Logger.LogError("A signing key was ignored because it was unable " +
                                            "to provide the requested RSA instance.");

                    continue;
                }

                // Export the RSA public key to create a new JSON Web Key
                // exposing the exponent and the modulus parameters.
                var parameters = algorithm.ExportParameters(includePrivateParameters: false);
                Debug.Assert(parameters.Exponent != null, "A null exponent was returned by RSA.ExportParameters()");
                Debug.Assert(parameters.Modulus != null, "A null modulus was returned by RSA.ExportParameters()");

                var key = new JsonWebKey {
                    Use = JsonWebKeyUseNames.Sig,
                    Kty = JsonWebAlgorithmsKeyTypes.RSA,

                    // Resolve the JWA identifier from the algorithm specified in the credentials.
                    Alg = OpenIdConnectServerHelpers.GetJwtAlgorithm(credentials.SignatureAlgorithm),

                    // Use the key identifier specified
                    // in the signing credentials.
                    Kid = identifier?.LocalId,

                    // Both E and N must be base64url-encoded.
                    // See http://tools.ietf.org/html/draft-ietf-jose-json-web-key-31#appendix-A.1
                    E = Base64UrlEncoder.Encode(parameters.Exponent),
                    N = Base64UrlEncoder.Encode(parameters.Modulus)
                };

                X509Certificate2 x509Certificate = null;

                // Determine whether the signing credentials are directly based on a X.509 certificate.
                var x509SigningCredentials = credentials as X509SigningCredentials;
                if (x509SigningCredentials != null) {
                    x509Certificate = x509SigningCredentials.Certificate;
                }

                // Skip looking for a X509SecurityKey in SigningCredentials.SigningKey
                // if a certificate has been found in the SigningCredentials instance.
                if (x509Certificate == null) {
                    // Determine whether the security key is an asymmetric key embedded in a X.509 certificate.
                    var x509SecurityKey = credentials.SigningKey as X509SecurityKey;
                    if (x509SecurityKey != null) {
                        x509Certificate = x509SecurityKey.Certificate;
                    }
                }

                // Skip looking for a X509AsymmetricSecurityKey in SigningCredentials.SigningKey
                // if a certificate has been found in SigningCredentials or SigningCredentials.SigningKey.
                if (x509Certificate == null) {
                    // Determine whether the security key is an asymmetric key embedded in a X.509 certificate.
                    var x509AsymmetricSecurityKey = credentials.SigningKey as X509AsymmetricSecurityKey;
                    if (x509AsymmetricSecurityKey != null) {
                        // The X.509 certificate is not directly accessible when using X509AsymmetricSecurityKey.
                        // Reflection is the only way to get the certificate used to create the security key.
                        var field = typeof(X509AsymmetricSecurityKey).GetField(
                            name: "certificate",
                            bindingAttr: BindingFlags.Instance | BindingFlags.NonPublic);
                        Debug.Assert(field != null);

                        x509Certificate = (X509Certificate2) field.GetValue(x509AsymmetricSecurityKey);
                    }
                }

                // If the signing key is embedded in a X.509 certificate, set
                // the x5t and x5c parameters using the certificate details.
                if (x509Certificate != null) {
                    // x5t must be base64url-encoded.
                    // See http://tools.ietf.org/html/draft-ietf-jose-json-web-key-31#section-4.8
                    key.X5t = Base64UrlEncoder.Encode(x509Certificate.GetCertHash());

                    // Unlike E or N, the certificates contained in x5c
                    // must be base64-encoded and not base64url-encoded.
                    // See http://tools.ietf.org/html/draft-ietf-jose-json-web-key-31#section-4.7
                    key.X5c.Add(Convert.ToBase64String(x509Certificate.RawData));
                }

                notification.Keys.Add(key);
            }

            await Options.Provider.HandleCryptographyRequest(notification);

            if (notification.HandledResponse) {
                return true;
            }

            else if (notification.Skipped) {
                return false;
            }

            var response = new JObject();
            var keys = new JArray();

            foreach (var key in notification.Keys) {
                var item = new JObject();

                // Ensure a key type has been provided.
                // See http://tools.ietf.org/html/draft-ietf-jose-json-web-key-31#section-4.1
                if (string.IsNullOrEmpty(key.Kty)) {
                    Options.Logger.LogError("A JSON Web Key was excluded from the key set because " +
                                            "it didn't contain the mandatory 'kid' parameter.");

                    continue;
                }

                // Create a dictionary associating the
                // JsonWebKey components with their values.
                var parameters = new Dictionary<string, string> {
                    { JsonWebKeyParameterNames.Kid, key.Kid },
                    { JsonWebKeyParameterNames.Use, key.Use },
                    { JsonWebKeyParameterNames.Kty, key.Kty },
                    { JsonWebKeyParameterNames.KeyOps, key.KeyOps },
                    { JsonWebKeyParameterNames.Alg, key.Alg },
                    { JsonWebKeyParameterNames.E, key.E },
                    { JsonWebKeyParameterNames.N, key.N },
                    { JsonWebKeyParameterNames.X5t, key.X5t },
                    { JsonWebKeyParameterNames.X5u, key.X5u }
                };

                foreach (var parameter in parameters) {
                    if (!string.IsNullOrEmpty(parameter.Value)) {
                        item.Add(parameter.Key, parameter.Value);
                    }
                }

                if (key.X5c.Count != 0) {
                    item.Add(JsonWebKeyParameterNames.X5c, JArray.FromObject(key.X5c));
                }

                keys.Add(item);
            }

            response.Add(JsonWebKeyParameterNames.Keys, keys);

            return await SendCryptographyResponseAsync(request, response);
        }

        private Task<bool> SendConfigurationResponseAsync(OpenIdConnectMessage request, OpenIdConnectMessage response) {
            var payload = new JObject();

            foreach (var parameter in response.Parameters) {
                payload[parameter.Key] = parameter.Value;
            }

            return SendConfigurationResponseAsync(request, payload);
        }

        private async Task<bool> SendConfigurationResponseAsync(OpenIdConnectMessage request, JObject response) {
            if (request == null) {
                request = new OpenIdConnectMessage();
            }

            var notification = new ApplyConfigurationResponseContext(Context, Options, request, response);
            await Options.Provider.ApplyConfigurationResponse(notification);

            if (notification.HandledResponse) {
                return true;
            }

            else if (notification.Skipped) {
                return false;
            }

            return await SendPayloadAsync(response);
        }

        private Task<bool> SendCryptographyResponseAsync(OpenIdConnectMessage request, OpenIdConnectMessage response) {
            var payload = new JObject();

            foreach (var parameter in response.Parameters) {
                payload[parameter.Key] = parameter.Value;
            }

            return SendCryptographyResponseAsync(request, payload);
        }

        private async Task<bool> SendCryptographyResponseAsync(OpenIdConnectMessage request, JObject response) {
            if (request == null) {
                request = new OpenIdConnectMessage();
            }

            var notification = new ApplyCryptographyResponseContext(Context, Options, request, response);
            await Options.Provider.ApplyCryptographyResponse(notification);

            if (notification.HandledResponse) {
                return true;
            }

            else if (notification.Skipped) {
                return false;
            }

            return await SendPayloadAsync(response);
        }
    }
}
