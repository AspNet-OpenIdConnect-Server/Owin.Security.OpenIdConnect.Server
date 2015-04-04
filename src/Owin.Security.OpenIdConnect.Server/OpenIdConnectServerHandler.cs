/*
 * Licensed under the Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0)
 * See https://github.com/AspNet-OpenIdConnect-Server/Owin.Security.OpenIdConnect.Server
 * for more information concerning the license and the contributors participating to this project.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IdentityModel.Protocols.WSTrust;
using System.IdentityModel.Tokens;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Microsoft.IdentityModel.Protocols;
using Microsoft.Owin.Infrastructure;
using Microsoft.Owin.Logging;
using Microsoft.Owin.Security;
using Microsoft.Owin.Security.Infrastructure;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Owin.Security.OpenIdConnect.Extensions;

namespace Owin.Security.OpenIdConnect.Server {
    internal class OpenIdConnectServerHandler : AuthenticationHandler<OpenIdConnectServerOptions> {
        private readonly ILogger logger;
        private bool headersSent;

        public OpenIdConnectServerHandler(ILogger logger) {
            this.logger = logger;
        }

        protected override Task<AuthenticationTicket> AuthenticateCoreAsync() {
            return Task.FromResult<AuthenticationTicket>(null);
        }

        public override async Task<bool> InvokeAsync() {
            var matchRequestContext = new MatchEndpointNotification(Context, Options);

            if (Options.AuthorizationEndpointPath == Request.Path) {
                matchRequestContext.MatchesAuthorizationEndpoint();
            }

            else if (Options.ConfigurationEndpointPath.HasValue &&
                     Options.ConfigurationEndpointPath == Request.Path) {
                matchRequestContext.MatchesConfigurationEndpoint();
            }

            else if (Options.CryptographyEndpointPath.HasValue &&
                     Options.CryptographyEndpointPath == Request.Path) {
                matchRequestContext.MatchesCryptographyEndpoint();
            }

            else if (Options.TokenEndpointPath.HasValue &&
                     Options.TokenEndpointPath == Request.Path) {
                matchRequestContext.MatchesTokenEndpoint();
            }

            await Options.Provider.MatchEndpoint(matchRequestContext);

            // Stop processing the request if MatchEndpoint called RequestCompleted.
            if (matchRequestContext.IsRequestCompleted) {
                return true;
            }

            if (matchRequestContext.IsAuthorizationEndpoint || matchRequestContext.IsConfigurationEndpoint ||
                matchRequestContext.IsCryptographyEndpoint || matchRequestContext.IsTokenEndpoint) {
                if (!Options.AllowInsecureHttp && string.Equals(Request.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)) {
                    logger.WriteWarning("Authorization server ignoring http request because AllowInsecureHttp is false.");
                    return false;
                }

                if (matchRequestContext.IsAuthorizationEndpoint) {
                    return await InvokeAuthorizationEndpointAsync();
                }

                if (matchRequestContext.IsConfigurationEndpoint) {
                    await InvokeConfigurationEndpointAsync();
                    return true;
                }

                if (matchRequestContext.IsCryptographyEndpoint) {
                    await InvokeCryptographyEndpointAsync();
                    return true;
                }

                if (matchRequestContext.IsTokenEndpoint) {
                    await InvokeTokenEndpointAsync();
                    return true;
                }
            }

            return false;
        }

        private async Task<bool> InvokeAuthorizationEndpointAsync() {
            OpenIdConnectMessage request = null;

            if (!string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Request.Method, "POST", StringComparison.OrdinalIgnoreCase)) {
                return await SendErrorPageAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "A malformed authorization request has been received: " +
                        "make sure to use either GET or POST."
                });
            }

            if (string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase)) {
                request = new OpenIdConnectMessage(Request.Query) {
                    RequestType = OpenIdConnectRequestType.AuthenticationRequest
                };
            }

            else {
                // See http://openid.net/specs/openid-connect-core-1_0.html#FormSerialization
                if (string.IsNullOrWhiteSpace(Request.ContentType)) {
                    return await SendErrorPageAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed authorization request has been received: " +
                            "the mandatory 'Content-Type' header was missing from the POST request."
                    });
                }

                // May have media/type; charset=utf-8, allow partial match.
                if (!Request.ContentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)) {
                    return await SendErrorPageAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed authorization request has been received: " +
                            "the 'Content-Type' header contained an unexcepted value. " +
                            "Make sure to use 'application/x-www-form-urlencoded'."
                    });
                }

                request = new OpenIdConnectMessage(await Request.ReadFormAsync()) {
                    RequestType = OpenIdConnectRequestType.AuthenticationRequest
                };
            }

            // Insert the authorization request in the OWIN context.
            Context.SetOpenIdConnectRequest(request);

            // While redirect_uri was not mandatory in OAuth2, this parameter
            // is now declared as REQUIRED and SHOULD cause an error when missing.
            // That said, the OIDC specs explicitly allow an authorization server to handle
            // a token request when redirect_uri was missing from the authorization request.
            // See http://openid.net/specs/openid-connect-core-1_0.html#AuthRequest
            // and http://openid.net/specs/openid-connect-core-1_0.html#TokenRequestValidation
            if (!string.IsNullOrEmpty(request.RedirectUri)) {
                Uri uri;
                if (!Uri.TryCreate(request.RedirectUri, UriKind.Absolute, out uri)) {
                    // redirect_uri MUST be an absolute URI.
                    // See http://tools.ietf.org/html/rfc6749#section-3.1.2
                    // and http://openid.net/specs/openid-connect-core-1_0.html#AuthRequest
                    return await SendErrorPageAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "redirect_uri must be absolute"
                    });
                }

                else if (!string.IsNullOrEmpty(uri.Fragment)) {
                    // redirect_uri MUST NOT include a fragment component.
                    // See http://tools.ietf.org/html/rfc6749#section-3.1.2
                    // and http://openid.net/specs/openid-connect-core-1_0.html#AuthRequest
                    return await SendErrorPageAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "redirect_uri must not include a fragment"
                    });
                }

                else if (!Options.AllowInsecureHttp && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)) {
                    // redirect_uri SHOULD require the use of TLS
                    // http://tools.ietf.org/html/rfc6749#section-3.1.2.1
                    // and http://openid.net/specs/openid-connect-core-1_0.html#AuthRequest
                    return await SendErrorPageAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "redirect_uri does not meet the security requirements"
                    });
                }
            }

            var clientContext = new ValidateClientRedirectUriNotification(Context, Options, request);
            await Options.Provider.ValidateClientRedirectUri(clientContext);

            if (!clientContext.IsValidated) {
                // Remove the unvalidated redirect_uri
                // from the authorization request.
                request.RedirectUri = null;

                // Update the authorization request in the OWIN context.
                Context.SetOpenIdConnectRequest(request);

                logger.WriteVerbose("Unable to validate client information");

                return await SendErrorPageAsync(new OpenIdConnectMessage {
                    Error = clientContext.Error,
                    ErrorDescription = clientContext.ErrorDescription,
                    ErrorUri = clientContext.ErrorUri
                });
            }

            if (string.IsNullOrEmpty(request.ResponseType)) {
                logger.WriteVerbose("Authorization request missing required response_type parameter");

                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "response_type parameter missing",
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            if (!request.IsAuthorizationCodeFlow() && !request.IsImplicitFlow() && !request.IsHybridFlow()) {
                logger.WriteVerbose("Authorization request contains unsupported response_type parameter");

                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.UnsupportedResponseType,
                    ErrorDescription = "response_type unsupported",
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            if (!request.IsFormPostResponseMode() && !request.IsFragmentResponseMode() && !request.IsQueryResponseMode()) {
                logger.WriteVerbose("Authorization request contains unsupported response_mode parameter");

                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "response_mode unsupported",
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            if (request.ContainsResponseType(OpenIdConnectConstants.ResponseTypes.IdToken) && !request.ContainsScope(OpenIdConnectScopes.OpenId)) {
                logger.WriteVerbose("The 'openid' scope part was missing");

                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "openid scope missing",
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            if (request.ContainsResponseType(OpenIdConnectConstants.ResponseTypes.Code) && !Options.TokenEndpointPath.HasValue) {
                logger.WriteVerbose("Authorization request contains the disabled code response_type");

                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.UnsupportedResponseType,
                    ErrorDescription = "response_type=code is not supported by this server",
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            if (request.ContainsResponseType(OpenIdConnectConstants.ResponseTypes.IdToken) && Options.SigningCredentials == null) {
                logger.WriteVerbose("Authorization request contains the disabled id_token response_type");

                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.UnsupportedResponseType,
                    ErrorDescription = "response_type=id_token is not supported by this server",
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            var validatingContext = new ValidateAuthorizationRequestNotification(Context, Options, request, clientContext);
            await Options.Provider.ValidateAuthorizationRequest(validatingContext);

            // Stop processing the request if Validated was not called.
            if (!validatingContext.IsValidated) {
                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage {
                    Error = validatingContext.Error,
                    ErrorDescription = validatingContext.ErrorDescription,
                    ErrorUri = validatingContext.ErrorUri,
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            var authorizationEndpointContext = new AuthorizationEndpointNotification(Context, Options, request);
            await Options.Provider.AuthorizationEndpoint(authorizationEndpointContext);

            // Update the authorization request in the OWIN context.
            Context.SetOpenIdConnectRequest(request);

            // Stop processing the request if AuthorizationEndpoint called RequestCompleted.
            if (authorizationEndpointContext.IsRequestCompleted) {
                return true;
            }

            return false;
        }

        protected override async Task InitializeCoreAsync() {
            Response.OnSendingHeaders(state => {
                var handler = (OpenIdConnectServerHandler) state;
                handler.headersSent = true;
            }, this);

            await base.InitializeCoreAsync();
        }

        /// <remarks>
        /// Authentication handlers cannot reliabily write to the response stream
        /// from ApplyResponseGrantAsync or ApplyResponseChallengeAsync because these methods
        /// are susceptible to be invoked from AuthenticationHandler.OnSendingHeaderCallback
        /// where calling Write or WriteAsync on the response stream may result in a deadlock
        /// on hosts using streamed responses. To work around this limitation, OpenIdConnectServerHandler
        /// doesn't implement ApplyResponseGrantAsync but TeardownCoreAsync,
        /// which is never called by AuthenticationHandler.OnSendingHeaderCallback.
        /// In theory, this would prevent OpenIdConnectServerHandler from both applying
        /// the response grant and allowing the next middleware in the pipeline to alter
        /// the response stream but in practice, the OpenIdConnectServerHandler is assumed to be
        /// the only middleware allowed to write to the response stream when a response grant has been applied.
        /// </remarks>
        protected override async Task TeardownCoreAsync() {
            // request may be null when no authorization request has been received
            // or has been already handled by InvokeAuthorizationEndpointAsync.
            var request = Context.GetOpenIdConnectRequest();
            if (request == null) {
                return;
            }

            // Stop processing the request if an authorization response has been forged by the inner application.
            // This allows the next middleware to return an OpenID Connect error or a custom response to the client.
            var response = Context.GetOpenIdConnectResponse();
            if (response != null && !string.IsNullOrWhiteSpace(response.RedirectUri)) {
                if (!string.IsNullOrWhiteSpace(response.Error)) {
                    await SendErrorRedirectAsync(request, response);
                    return;
                }

                await ApplyAuthorizationResponseAsync(request, response);
                return;
            }

            // Stop processing the request if there's no response grant that matches
            // the authentication type associated with this middleware instance
            // or if the response status code doesn't indicate a successful response.
            var context = Helper.LookupSignIn(Options.AuthenticationType);
            if (context == null || Response.StatusCode != 200) {
                return;
            }

            if (headersSent) {
                logger.WriteCritical(
                    "OpenIdConnectServerHandler.TeardownCoreAsync cannot be called when " +
                    "the response headers have already been sent back to the user agent. " +
                    "Make sure the response body has not been altered and that no middleware " +
                    "has attempted to write to the response stream during this request.");
                return;
            }

            response = new OpenIdConnectMessage {
                ClientId = request.ClientId,
                Nonce = request.Nonce,
                RedirectUri = request.RedirectUri,
                State = request.State
            };
            
            // Associate client_id with all subsequent tickets.
            context.Properties.Dictionary[OpenIdConnectConstants.Extra.ClientId] = request.ClientId;
            var ticket = new AuthenticationTicket(context.Identity, context.Properties);

            if (!string.IsNullOrEmpty(request.RedirectUri)) {
                // Keep original request parameter for later comparison.
                context.Properties.Dictionary[OpenIdConnectConstants.Extra.RedirectUri] = request.RedirectUri;
            }

            // Determine whether an authorization code should be returned
            // and invoke CreateAuthorizationCodeAsync if necessary.
            if (request.ContainsResponseType(OpenIdConnectConstants.ResponseTypes.Code)) {
                response.Code = await CreateAuthorizationCodeAsync(ticket, request, response);
            }

            // Determine whether an access token should be returned
            // and invoke CreateAccessTokenAsync if necessary.
            if (request.ContainsResponseType(OpenIdConnectConstants.ResponseTypes.Token)) {
                response.TokenType = OpenIdConnectConstants.TokenTypes.Bearer;
                response.AccessToken = await CreateAccessTokenAsync(ticket, request, response);

                var accessTokenExpiresUtc = context.Properties.ExpiresUtc;
                if (accessTokenExpiresUtc.HasValue) {
                    var expiresTimeSpan = accessTokenExpiresUtc - Options.SystemClock.UtcNow;
                    var expiresIn = (long) (expiresTimeSpan.Value.TotalSeconds + .5);

                    response.ExpiresIn = expiresIn.ToString(CultureInfo.InvariantCulture);
                }
            }

            // Determine whether an identity token should be returned.
            if (request.ContainsResponseType(OpenIdConnectConstants.ResponseTypes.IdToken)) {
                response.IdToken = await CreateIdentityTokenAsync(ticket, request, response);
            }

            var authorizationEndpointResponseContext = new AuthorizationEndpointResponseNotification(Context, Options, request, response);
            await Options.Provider.AuthorizationEndpointResponse(authorizationEndpointResponseContext);

            // Stop processing the request if AuthorizationEndpointResponse called RequestCompleted.
            if (authorizationEndpointResponseContext.IsRequestCompleted) {
                return;
            }

            await ApplyAuthorizationResponseAsync(request, response);
        }

        private async Task<bool> ApplyAuthorizationResponseAsync(OpenIdConnectMessage request, OpenIdConnectMessage response) {
            if (request.IsFormPostResponseMode()) {
                using (var buffer = new MemoryStream())
                using (var writer = new StreamWriter(buffer)) {
                    writer.WriteLine("<!doctype html>");
                    writer.WriteLine("<html>");
                    writer.WriteLine("<body>");

                    // While the redirect_uri parameter should be guarded against unknown values
                    // by IOpenIdConnectServerProvider.ValidateClientRedirectUri,
                    // it's still safer to encode it to avoid cross-site scripting attacks
                    // if the authorization server has a relaxed policy concerning redirect URIs.
                    writer.WriteLine("<form name='form' method='post' action='" + WebUtility.HtmlEncode(response.RedirectUri) + "'>");

                    foreach (var parameter in response.Parameters) {
                        // Don't include client_id, redirect_uri or response_mode in the form.
                        if (string.Equals(parameter.Key, OpenIdConnectParameterNames.ClientId, StringComparison.Ordinal) ||
                            string.Equals(parameter.Key, OpenIdConnectParameterNames.RedirectUri, StringComparison.Ordinal) ||
                            string.Equals(parameter.Key, OpenIdConnectParameterNames.ResponseMode, StringComparison.Ordinal)) {
                            continue;
                        }

                        var key = WebUtility.HtmlEncode(parameter.Key);
                        var value = WebUtility.HtmlEncode(parameter.Value);

                        writer.WriteLine("<input type='hidden' name='" + key + "' value='" + value + "' />");
                    }

                    writer.WriteLine("<noscript>Click here to finish the authorization process: <input type='submit' /></noscript>");
                    writer.WriteLine("</form>");
                    writer.WriteLine("<script>document.form.submit();</script>");
                    writer.WriteLine("</body>");
                    writer.WriteLine("</html>");
                    writer.Flush();

                    Response.ContentLength = buffer.Length;
                    Response.ContentType = "text/html;charset=UTF-8";

                    buffer.Seek(offset: 0, loc: SeekOrigin.Begin);
                    await buffer.CopyToAsync(Response.Body, 4096, Request.CallCancelled);

                    return true;
                }
            }

            else if (request.IsFragmentResponseMode()) {
                string location = response.RedirectUri;
                var appender = new Appender(location, '#');

                foreach (var parameter in response.Parameters) {
                    // Don't include client_id, redirect_uri or response_mode in the fragment.
                    if (string.Equals(parameter.Key, OpenIdConnectParameterNames.ClientId, StringComparison.Ordinal) || 
                        string.Equals(parameter.Key, OpenIdConnectParameterNames.RedirectUri, StringComparison.Ordinal) ||
                        string.Equals(parameter.Key, OpenIdConnectParameterNames.ResponseMode, StringComparison.Ordinal)) {
                        continue;
                    }

                    appender.Append(parameter.Key, parameter.Value);
                }

                Response.Redirect(appender.ToString());
                return true;
            }

            else if (request.IsQueryResponseMode()) {
                string location = response.RedirectUri;

                foreach (var parameter in response.Parameters) {
                    // Don't include client_id, redirect_uri or response_mode in the query string.
                    if (string.Equals(parameter.Key, OpenIdConnectParameterNames.ClientId, StringComparison.Ordinal) || 
                        string.Equals(parameter.Key, OpenIdConnectParameterNames.RedirectUri, StringComparison.Ordinal) ||
                        string.Equals(parameter.Key, OpenIdConnectParameterNames.ResponseMode, StringComparison.Ordinal)) {
                        continue;
                    }

                    location = WebUtilities.AddQueryString(location, parameter.Key, parameter.Value);
                }

                Response.Redirect(location);
                return true;
            }

            return false;
        }

        private async Task InvokeConfigurationEndpointAsync() {
            var configurationEndpointContext = new ConfigurationEndpointNotification(Context, Options);
            configurationEndpointContext.Issuer = Options.Issuer + "/";

            // Metadata requests must be made via GET.
            // See http://openid.net/specs/openid-connect-discovery-1_0.html#ProviderConfigurationRequest
            if (!string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase)) {
                logger.WriteError(string.Format(CultureInfo.InvariantCulture,
                    "Configuration endpoint: invalid method '{0}' used", Request.Method));
                return;
            }

            // Set the default endpoints concatenating Options.Issuer and Options.*EndpointPath.
            if (Options.AuthorizationEndpointPath.HasValue) {
                configurationEndpointContext.AuthorizationEndpoint = Options.Issuer + Options.AuthorizationEndpointPath;
            }

            // While the jwks_uri parameter is in principle mandatory, many OIDC clients are known
            // to work in a degraded mode when this parameter is not provided in the JSON response.
            // Making it mandatory in Owin.Security.OpenIdConnect.Server would prevent the end developer from
            // using custom security keys and manage himself the token validation parameters in the OIDC client.
            // To avoid this issue, the jwks_uri parameter is only added to the response when the JWKS endpoint
            // is believed to provide a valid response, which is the case with asymmetric keys supporting RSA-SHA256.
            // See http://openid.net/specs/openid-connect-discovery-1_0.html#ProviderMetadata
            if (Options.CryptographyEndpointPath.HasValue &&
                Options.SigningCredentials != null &&
                Options.SigningCredentials.SigningKey is AsymmetricSecurityKey &&
                Options.SigningCredentials.SigningKey.IsSupportedAlgorithm(SecurityAlgorithms.RsaSha256Signature)) {
                configurationEndpointContext.KeyEndpoint = Options.Issuer + Options.CryptographyEndpointPath;
            }

            if (Options.TokenEndpointPath.HasValue) {
                configurationEndpointContext.TokenEndpoint = Options.Issuer + Options.TokenEndpointPath;
            }

            configurationEndpointContext.GrantTypes.Add(
                OpenIdConnectConstants.GrantTypes.Implicit);

            // Only expose the authorization code grant type if
            // the token endpoint has not been explicitly disabled.
            if (Options.TokenEndpointPath.HasValue) {
                configurationEndpointContext.GrantTypes.Add(
                    OpenIdConnectConstants.GrantTypes.AuthorizationCode);
            }

            configurationEndpointContext.ResponseModes.Add(
                OpenIdConnectConstants.ResponseModes.FormPost);
            configurationEndpointContext.ResponseModes.Add(
                OpenIdConnectConstants.ResponseModes.Fragment);
            configurationEndpointContext.ResponseModes.Add(
                OpenIdConnectConstants.ResponseModes.Query);

            configurationEndpointContext.ResponseTypes.Add(
                OpenIdConnectConstants.ResponseTypes.Token);

            // Only expose response types containing id_token when
            // signing credentials have been explicitly provided.
            if (Options.SigningCredentials != null) {
                configurationEndpointContext.ResponseTypes.Add(
                    OpenIdConnectConstants.ResponseTypes.IdToken);
                configurationEndpointContext.ResponseTypes.Add(
                    OpenIdConnectConstants.ResponseTypes.IdToken + ' ' +
                    OpenIdConnectConstants.ResponseTypes.Token);
            }

            // Only expose response types containing code when
            // the token endpoint has not been explicitly disabled.
            if (Options.TokenEndpointPath.HasValue) {
                configurationEndpointContext.ResponseTypes.Add(
                    OpenIdConnectConstants.ResponseTypes.Code);

                configurationEndpointContext.ResponseTypes.Add(
                    OpenIdConnectConstants.ResponseTypes.Code + ' ' +
                    OpenIdConnectConstants.ResponseTypes.Token);

                // Only expose response types containing id_token when
                // signing credentials have been explicitly provided.
                if (Options.SigningCredentials != null) {
                    configurationEndpointContext.ResponseTypes.Add(
                        OpenIdConnectConstants.ResponseTypes.Code + ' ' +
                        OpenIdConnectConstants.ResponseTypes.IdToken);

                    configurationEndpointContext.ResponseTypes.Add(
                        OpenIdConnectConstants.ResponseTypes.Code + ' ' +
                        OpenIdConnectConstants.ResponseTypes.IdToken + ' ' +
                        OpenIdConnectConstants.ResponseTypes.Token);
                }
            }

            configurationEndpointContext.Scopes.Add(OpenIdConnectScopes.OpenId);

            configurationEndpointContext.SubjectTypes.Add(OpenIdConnectConstants.SubjectTypes.Public);
            configurationEndpointContext.SubjectTypes.Add(OpenIdConnectConstants.SubjectTypes.Pairwise);

            configurationEndpointContext.SigningAlgorithms.Add(OpenIdConnectConstants.Algorithms.RS256);

            await Options.Provider.ConfigurationEndpoint(configurationEndpointContext);

            // Stop processing the request if
            // ConfigurationEndpoint called RequestCompleted.
            if (configurationEndpointContext.IsRequestCompleted) {
                return;
            }
            
            var payload = new JObject();

            payload.Add(OpenIdConnectConstants.Metadata.Issuer,
                configurationEndpointContext.Issuer);

            payload.Add(OpenIdConnectConstants.Metadata.AuthorizationEndpoint,
                configurationEndpointContext.AuthorizationEndpoint);

            if (!string.IsNullOrWhiteSpace(configurationEndpointContext.TokenEndpoint)) {
                payload.Add(OpenIdConnectConstants.Metadata.TokenEndpoint,
                    configurationEndpointContext.TokenEndpoint);
            }

            if (!string.IsNullOrWhiteSpace(configurationEndpointContext.KeyEndpoint)) {
                payload.Add(OpenIdConnectConstants.Metadata.JwksUri,
                    configurationEndpointContext.KeyEndpoint);
            }

            payload.Add(OpenIdConnectConstants.Metadata.GrantTypesSupported,
                JArray.FromObject(configurationEndpointContext.GrantTypes));

            payload.Add(OpenIdConnectConstants.Metadata.ResponseModesSupported,
                JArray.FromObject(configurationEndpointContext.ResponseModes));

            payload.Add(OpenIdConnectConstants.Metadata.ResponseTypesSupported,
                JArray.FromObject(configurationEndpointContext.ResponseTypes));

            payload.Add(OpenIdConnectConstants.Metadata.SubjectTypesSupported,
                JArray.FromObject(configurationEndpointContext.SubjectTypes));

            payload.Add(OpenIdConnectConstants.Metadata.ScopesSupported,
                JArray.FromObject(configurationEndpointContext.Scopes));

            payload.Add(OpenIdConnectConstants.Metadata.IdTokenSigningAlgValuesSupported,
                JArray.FromObject(configurationEndpointContext.SigningAlgorithms));

            var configurationEndpointResponseContext = new ConfigurationEndpointResponseNotification(Context, Options, payload);
            await Options.Provider.ConfigurationEndpointResponse(configurationEndpointResponseContext);

            // Stop processing the request if ConfigurationEndpointResponse called RequestCompleted.
            if (configurationEndpointResponseContext.IsRequestCompleted) {
                return;
            }

            using (var buffer = new MemoryStream())
            using (var writer = new JsonTextWriter(new StreamWriter(buffer))) {
                payload.WriteTo(writer);
                writer.Flush();

                Response.ContentLength = buffer.Length;
                Response.ContentType = "application/json;charset=UTF-8";

                buffer.Seek(offset: 0, loc: SeekOrigin.Begin);
                await buffer.CopyToAsync(Response.Body, 4096, Request.CallCancelled);
            }
        }

        private async Task InvokeCryptographyEndpointAsync() {
            var cryptographyEndpointNotification = new CryptographyEndpointNotification(Context, Options);

            // Metadata requests must be made via GET.
            // See http://openid.net/specs/openid-connect-discovery-1_0.html#ProviderConfigurationRequest
            if (!string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase)) {
                logger.WriteError(string.Format(CultureInfo.InvariantCulture,
                    "Keys endpoint: invalid method '{0}' used", Request.Method));
                return;
            }

            if (Options.SigningCredentials == null) {
                logger.WriteError("Keys endpoint: no signing credentials provided. " +
                    "Make sure valid credentials are assigned to Options.SigningCredentials.");
                return;
            }

            // Skip processing the metadata request if no supported key can be found.
            // Note: SigningKey is assumed to be never null under normal circonstances,
            // given that an initial check is made by SigningCredentials's constructor.
            // The SigningCredentials property is itself guarded against null values
            // in OpenIdConnectServerMiddleware's constructor.
            var asymmetricSecurityKey = Options.SigningCredentials.SigningKey as AsymmetricSecurityKey;
            if (asymmetricSecurityKey == null) {
                logger.WriteError(string.Format(CultureInfo.InvariantCulture,
                    "Keys endpoint: invalid signing key registered. " +
                    "Make sure to provide an asymmetric security key deriving from '{0}'.",
                    typeof(AsymmetricSecurityKey).FullName));
                return;
            }

            if (!asymmetricSecurityKey.IsSupportedAlgorithm(SecurityAlgorithms.RsaSha256Signature)) {
                logger.WriteError(string.Format(CultureInfo.InvariantCulture,
                    "Keys endpoint: invalid signing key registered. " +
                    "Make sure to provide a '{0}' instance exposing " +
                    "an asymmetric security key supporting the '{1}' algorithm.",
                    typeof(SigningCredentials).Name, SecurityAlgorithms.RsaSha256Signature));
                return;
            }

            X509Certificate2 x509Certificate = null;

            // Determine whether the signing credentials are directly based on a X.509 certificate.
            var x509SigningCredentials = Options.SigningCredentials as X509SigningCredentials;
            if (x509SigningCredentials != null) {
                x509Certificate = x509SigningCredentials.Certificate;
            }

            // Skip looking for a X509SecurityKey in SigningCredentials.SigningKey
            // if a certificate has been found in the SigningCredentials instance.
            if (x509Certificate == null) {
                // Determine whether the security key is an asymmetric key embedded in a X.509 certificate.
                var x509SecurityKey = Options.SigningCredentials.SigningKey as X509SecurityKey;
                if (x509SecurityKey != null) {
                    x509Certificate = x509SecurityKey.Certificate;
                }
            }

            // Skip looking for a X509AsymmetricSecurityKey in SigningCredentials.SigningKey
            // if a certificate has been found in SigningCredentials or SigningCredentials.SigningKey.
            if (x509Certificate == null) {
                // Determine whether the security key is an asymmetric key embedded in a X.509 certificate.
                var x509AsymmetricSecurityKey = Options.SigningCredentials.SigningKey as X509AsymmetricSecurityKey;
                if (x509AsymmetricSecurityKey != null) {
                    // The X.509 certificate is not directly accessible when using X509AsymmetricSecurityKey.
                    // Reflection is the only way to get the certificate used to create the security key.
                    var field = typeof(X509AsymmetricSecurityKey).GetField(
                        name: "certificate",
                        bindingAttr: BindingFlags.Instance | BindingFlags.NonPublic);

                    x509Certificate = (X509Certificate2) field.GetValue(x509AsymmetricSecurityKey);
                }
            }

            if (x509Certificate != null) {
                // Create a new JSON Web Key exposing the
                // certificate instead of its public RSA key.
                cryptographyEndpointNotification.Keys.Add(new JsonWebKey {
                    Kty = JsonWebAlgorithmsKeyTypes.RSA,
                    Alg = JwtAlgorithms.RSA_SHA256,
                    Use = JsonWebKeyUseNames.Sig,

                    // x5t must be base64url-encoded.
                    // See http://tools.ietf.org/html/draft-ietf-jose-json-web-key-31#section-4.8
                    X5t = Base64UrlEncoder.Encode(x509Certificate.GetCertHash()),

                    // Unlike E or N, the certificates contained in x5c
                    // must be base64-encoded and not base64url-encoded.
                    // See http://tools.ietf.org/html/draft-ietf-jose-json-web-key-31#section-4.7
                    X5c = { Convert.ToBase64String(x509Certificate.RawData) }
                });
            }

            else {
                // Create a new JSON Web Key exposing the exponent and the modulus of the RSA public key.
                var asymmetricAlgorithm = (RSA) asymmetricSecurityKey.GetAsymmetricAlgorithm(
                    algorithm: SecurityAlgorithms.RsaSha256Signature, privateKey: false);

                // Export the RSA public key.
                var parameters = asymmetricAlgorithm.ExportParameters(
                    includePrivateParameters: false);

                cryptographyEndpointNotification.Keys.Add(new JsonWebKey {
                    Kty = JsonWebAlgorithmsKeyTypes.RSA,
                    Alg = JwtAlgorithms.RSA_SHA256,
                    Use = JsonWebKeyUseNames.Sig,

                    // Both E and N must be base64url-encoded.
                    // See http://tools.ietf.org/html/draft-ietf-jose-json-web-key-31#appendix-A.1
                    E = Base64UrlEncoder.Encode(parameters.Exponent),
                    N = Base64UrlEncoder.Encode(parameters.Modulus)
                });
            }

            await Options.Provider.CryptographyEndpoint(cryptographyEndpointNotification);

            // Skip processing the JWKS request if
            // RequestCompleted has been called.
            if (cryptographyEndpointNotification.IsRequestCompleted) {
                return;
            }

            // Ensure at least one key has been added to context.Keys.
            if (!cryptographyEndpointNotification.Keys.Any()) {
                logger.WriteError("Keys endpoint: no JSON Web Key found.");
                return;
            }

            var payload = new JObject();
            var keys = new JArray();

            foreach (var key in cryptographyEndpointNotification.Keys) {
                var item = new JObject();

                // Ensure a key type has been provided.
                // See http://tools.ietf.org/html/draft-ietf-jose-json-web-key-31#section-4.1
                if (string.IsNullOrWhiteSpace(key.Kty)) {
                    logger.WriteWarning("Keys endpoint: a JSON Web Key didn't " +
                        "contain the mandatory 'Kty' parameter and has been ignored.");
                    continue;
                }

                // Create a dictionary associating the
                // JsonWebKey components with their values.
                var parameters = new Dictionary<string, string> {
                    { JsonWebKeyParameterNames.Kty, key.Kty },
                    { JsonWebKeyParameterNames.Alg, key.Alg },
                    { JsonWebKeyParameterNames.E, key.E },
                    { JsonWebKeyParameterNames.KeyOps, key.KeyOps },
                    { JsonWebKeyParameterNames.Kid, key.Kid },
                    { JsonWebKeyParameterNames.N, key.N },
                    { JsonWebKeyParameterNames.Use, key.Use },
                    { JsonWebKeyParameterNames.X5t, key.X5t },
                    { JsonWebKeyParameterNames.X5u, key.X5u },
                };

                foreach (var parameter in parameters) {
                    if (!string.IsNullOrEmpty(parameter.Value)) {
                        item.Add(parameter.Key, parameter.Value);
                    }
                }

                if (key.X5c.Any()) {
                    item.Add(JsonWebKeyParameterNames.X5c, JArray.FromObject(key.X5c));
                }

                keys.Add(item);
            }

            payload.Add(JsonWebKeyParameterNames.Keys, keys);

            var cryptographyEndpointResponseNotification = new CryptographyEndpointResponseNotification(Context, Options, payload);
            await Options.Provider.CryptographyEndpointResponse(cryptographyEndpointResponseNotification);

            // Skip processing the request if RequestCompleted has been called.
            if (cryptographyEndpointResponseNotification.IsRequestCompleted) {
                return;
            }

            using (var buffer = new MemoryStream())
            using (var writer = new JsonTextWriter(new StreamWriter(buffer))) {
                payload.WriteTo(writer);
                writer.Flush();

                Response.ContentLength = buffer.Length;
                Response.ContentType = "application/json;charset=UTF-8";

                buffer.Seek(offset: 0, loc: SeekOrigin.Begin);
                await buffer.CopyToAsync(Response.Body, 4096, Request.CallCancelled);
            }
        }

        private async Task InvokeTokenEndpointAsync() {
            if (!string.Equals(Request.Method, "POST", StringComparison.OrdinalIgnoreCase)) {
                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "A malformed token request has been received: make sure to use POST."
                });

                return;
            }

            // See http://openid.net/specs/openid-connect-core-1_0.html#FormSerialization
            if (string.IsNullOrWhiteSpace(Request.ContentType)) {
                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "A malformed token request has been received: " +
                        "the mandatory 'Content-Type' header was missing from the POST request."
                });

                return;
            }

            // May have media/type; charset=utf-8, allow partial match.
            if (!Request.ContentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)) {
                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "A malformed token request has been received: " +
                        "the 'Content-Type' header contained an unexcepted value. " +
                        "Make sure to use 'application/x-www-form-urlencoded'."
                });

                return;
            }

            var request = new OpenIdConnectMessage(await Request.ReadFormAsync());
            request.RequestType = OpenIdConnectRequestType.TokenRequest;

            // Remove milliseconds in case they don't round-trip
            var currentUtc = Options.SystemClock.UtcNow;
            currentUtc = currentUtc.Subtract(TimeSpan.FromMilliseconds(currentUtc.Millisecond));

            var clientContext = new ValidateClientAuthenticationNotification(Context, Options, request);
            await Options.Provider.ValidateClientAuthentication(clientContext);

            if (!clientContext.IsValidated) {
                logger.WriteError("clientID is not valid.");

                if (!clientContext.HasError) {
                    clientContext.SetError(OpenIdConnectConstants.Errors.InvalidClient);
                }

                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = clientContext.Error,
                    ErrorDescription = clientContext.ErrorDescription,
                    ErrorUri = clientContext.ErrorUri
                });

                return;
            }

            var validatingContext = new ValidateTokenRequestNotification(Context, Options, request, clientContext);

            AuthenticationTicket ticket = null;
            if (request.IsAuthorizationCodeGrantType()) {
                // Authorization Code Grant http://tools.ietf.org/html/rfc6749#section-4.1
                // Access Token Request http://tools.ietf.org/html/rfc6749#section-4.1.3
                ticket = await InvokeTokenEndpointAuthorizationCodeGrantAsync(validatingContext, currentUtc);
            }

            else if (request.IsPasswordGrantType()) {
                // Resource Owner Password Credentials Grant http://tools.ietf.org/html/rfc6749#section-4.3
                // Access Token Request http://tools.ietf.org/html/rfc6749#section-4.3.2
                ticket = await InvokeTokenEndpointResourceOwnerPasswordCredentialsGrantAsync(validatingContext, currentUtc);
            }

            else if (request.IsClientCredentialsGrantType()) {
                // Client Credentials Grant http://tools.ietf.org/html/rfc6749#section-4.4
                // Access Token Request http://tools.ietf.org/html/rfc6749#section-4.4.2
                ticket = await InvokeTokenEndpointClientCredentialsGrantAsync(validatingContext, currentUtc);
            }

            else if (request.IsRefreshTokenGrantType()) {
                // Refreshing an Access Token
                // http://tools.ietf.org/html/rfc6749#section-6
                ticket = await InvokeTokenEndpointRefreshTokenGrantAsync(validatingContext, currentUtc);
            }

            else if (!string.IsNullOrWhiteSpace(request.GrantType)) {
                // Defining New Authorization Grant Types
                // http://tools.ietf.org/html/rfc6749#section-8.3
                ticket = await InvokeTokenEndpointCustomGrantAsync(validatingContext, currentUtc);
            }

            else {
                // Error Response http://tools.ietf.org/html/rfc6749#section-5.2
                // The authorization grant type is not supported by the
                // authorization server.
                logger.WriteError("grant type is not recognized");
                validatingContext.SetError(OpenIdConnectConstants.Errors.UnsupportedGrantType);
            }

            if (ticket == null) {
                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = validatingContext.Error,
                    ErrorDescription = validatingContext.ErrorDescription,
                    ErrorUri = validatingContext.ErrorUri
                });

                return;
            }

            ticket.Properties.IssuedUtc = currentUtc;
            ticket.Properties.ExpiresUtc = currentUtc.Add(Options.AccessTokenLifetime);

            var tokenEndpointContext = new TokenEndpointNotification(Context, Options, ticket, request);
            await Options.Provider.TokenEndpoint(tokenEndpointContext);

            // Stop processing the request if
            // TokenEndpoint called RequestCompleted.
            if (tokenEndpointContext.IsRequestCompleted) {
                return;
            }
            
            // Flow the changes made to the ticket.
            ticket = tokenEndpointContext.Ticket;

            var response = new OpenIdConnectMessage();
            response.TokenType = OpenIdConnectConstants.TokenTypes.Bearer;
            response.AccessToken = await CreateAccessTokenAsync(ticket, request, response);
            response.IdToken = await CreateIdentityTokenAsync(ticket, request, response);

            // Only issue a new refresh token if sliding expiration
            // is enabled or if a different grant type has been used.
            if (!request.IsRefreshTokenGrantType() || Options.UseSlidingExpiration) {
                response.SetParameter(OpenIdConnectConstants.Parameters.RefreshToken,
                    await CreateRefreshTokenAsync(ticket, request, response));
            }

            var accessTokenExpiresUtc = ticket.Properties.ExpiresUtc;
            if (accessTokenExpiresUtc.HasValue) {
                var expiresTimeSpan = accessTokenExpiresUtc - currentUtc;

                var expiresIn = (long) expiresTimeSpan.Value.TotalSeconds;
                if (expiresIn > 0) {
                    response.ExpiresIn = expiresIn.ToString(CultureInfo.InvariantCulture);
                }
            }
            
            var payload = new JObject();

            foreach (var parameter in response.Parameters) {
                payload.Add(parameter.Key, parameter.Value);
            }
            
            var tokenEndpointResponseContext = new TokenEndpointResponseNotification(Context, Options, payload);
            await Options.Provider.TokenEndpointResponse(tokenEndpointResponseContext);

            // Stop processing the request if
            // TokenEndpointResponse called RequestCompleted.
            if (tokenEndpointResponseContext.IsRequestCompleted) {
                return;
            }

            using (var buffer = new MemoryStream())
            using (var writer = new JsonTextWriter(new StreamWriter(buffer))) {
                payload.WriteTo(writer);
                writer.Flush();

                Response.ContentLength = buffer.Length;
                Response.ContentType = "application/json;charset=UTF-8";

                Response.Headers.Set("Cache-Control", "no-cache");
                Response.Headers.Set("Pragma", "no-cache");
                Response.Headers.Set("Expires", "-1");

                buffer.Seek(offset: 0, loc: SeekOrigin.Begin);
                await buffer.CopyToAsync(Response.Body, 4096, Request.CallCancelled);
            }
        }

        private async Task<AuthenticationTicket> InvokeTokenEndpointAuthorizationCodeGrantAsync(
            ValidateTokenRequestNotification validatingContext, DateTimeOffset currentUtc) {
            OpenIdConnectMessage tokenRequest = validatingContext.TokenRequest;
            
            var ticket = await ReceiveAuthorizationCodeAsync(tokenRequest.Code);
            if (ticket == null) {
                logger.WriteError("invalid authorization code");
                validatingContext.SetError(OpenIdConnectConstants.Errors.InvalidGrant);
                return null;
            }

            if (!ticket.Properties.ExpiresUtc.HasValue ||
                ticket.Properties.ExpiresUtc < currentUtc) {
                logger.WriteError("expired authorization code");
                validatingContext.SetError(OpenIdConnectConstants.Errors.InvalidGrant);
                return null;
            }

            string clientId;
            if (!ticket.Properties.Dictionary.TryGetValue(OpenIdConnectConstants.Extra.ClientId, out clientId) ||
                !string.Equals(clientId, validatingContext.ClientContext.ClientId, StringComparison.Ordinal)) {
                logger.WriteError("authorization code does not contain matching client_id");
                validatingContext.SetError(OpenIdConnectConstants.Errors.InvalidGrant);
                return null;
            }

            string redirectUri;
            if (ticket.Properties.Dictionary.TryGetValue(OpenIdConnectConstants.Extra.RedirectUri, out redirectUri)) {
                ticket.Properties.Dictionary.Remove(OpenIdConnectConstants.Extra.RedirectUri);
                if (!string.Equals(redirectUri, tokenRequest.RedirectUri, StringComparison.Ordinal)) {
                    logger.WriteError("authorization code does not contain matching redirect_uri");
                    validatingContext.SetError(OpenIdConnectConstants.Errors.InvalidGrant);
                    return null;
                }
            }

            await Options.Provider.ValidateTokenRequest(validatingContext);

            var grantContext = new GrantAuthorizationCodeNotification(Context, Options, tokenRequest, ticket);

            if (validatingContext.IsValidated) {
                await Options.Provider.GrantAuthorizationCode(grantContext);
            }

            return ReturnOutcome(
                validatingContext,
                grantContext,
                grantContext.Ticket,
                OpenIdConnectConstants.Errors.InvalidGrant);
        }

        private async Task<AuthenticationTicket> InvokeTokenEndpointResourceOwnerPasswordCredentialsGrantAsync(
            ValidateTokenRequestNotification validatingContext,
            DateTimeOffset currentUtc) {
            OpenIdConnectMessage tokenRequest = validatingContext.TokenRequest;

            await Options.Provider.ValidateTokenRequest(validatingContext);

            var grantContext = new GrantResourceOwnerCredentialsNotification(Context, Options, tokenRequest);

            if (validatingContext.IsValidated) {
                await Options.Provider.GrantResourceOwnerCredentials(grantContext);
            }

            return ReturnOutcome(
                validatingContext,
                grantContext,
                grantContext.Ticket,
                OpenIdConnectConstants.Errors.InvalidGrant);
        }

        private async Task<AuthenticationTicket> InvokeTokenEndpointClientCredentialsGrantAsync(
            ValidateTokenRequestNotification validatingContext,
            DateTimeOffset currentUtc) {
            OpenIdConnectMessage tokenRequest = validatingContext.TokenRequest;

            await Options.Provider.ValidateTokenRequest(validatingContext);
            if (!validatingContext.IsValidated) {
                return null;
            }

            var grantContext = new GrantClientCredentialsNotification(Context, Options, tokenRequest);

            await Options.Provider.GrantClientCredentials(grantContext);

            return ReturnOutcome(
                validatingContext,
                grantContext,
                grantContext.Ticket,
                OpenIdConnectConstants.Errors.UnauthorizedClient);
        }

        private async Task<AuthenticationTicket> InvokeTokenEndpointRefreshTokenGrantAsync(
            ValidateTokenRequestNotification validatingContext, DateTimeOffset currentUtc) {
            OpenIdConnectMessage tokenRequest = validatingContext.TokenRequest;

            var ticket = await ReceiveRefreshTokenAsync(tokenRequest.GetParameter("refresh_token"));
            if (ticket == null) {
                logger.WriteError("invalid refresh token");
                validatingContext.SetError(OpenIdConnectConstants.Errors.InvalidGrant);
                return null;
            }

            if (!ticket.Properties.ExpiresUtc.HasValue ||
                ticket.Properties.ExpiresUtc < currentUtc) {
                logger.WriteError("expired refresh token");
                validatingContext.SetError(OpenIdConnectConstants.Errors.InvalidGrant);
                return null;
            }

            await Options.Provider.ValidateTokenRequest(validatingContext);

            var grantContext = new GrantRefreshTokenNotification(Context, Options, tokenRequest, ticket);

            if (validatingContext.IsValidated) {
                await Options.Provider.GrantRefreshToken(grantContext);
            }

            return ReturnOutcome(
                validatingContext,
                grantContext,
                grantContext.Ticket,
                OpenIdConnectConstants.Errors.InvalidGrant);
        }

        private async Task<AuthenticationTicket> InvokeTokenEndpointCustomGrantAsync(
            ValidateTokenRequestNotification validatingContext,
            DateTimeOffset currentUtc) {
            OpenIdConnectMessage tokenRequest = validatingContext.TokenRequest;

            await Options.Provider.ValidateTokenRequest(validatingContext);

            var grantContext = new GrantCustomExtensionNotification(Context, Options, tokenRequest);

            if (validatingContext.IsValidated) {
                await Options.Provider.GrantCustomExtension(grantContext);
            }

            return ReturnOutcome(
                validatingContext,
                grantContext,
                grantContext.Ticket,
                OpenIdConnectConstants.Errors.UnsupportedGrantType);
        }

        private static string GenerateHash(string value, string algorithm = null) {
            using (var hashAlgorithm = HashAlgorithm.Create(algorithm)) {
                byte[] hashBytes = hashAlgorithm.ComputeHash(Encoding.UTF8.GetBytes(value));

                var hashString = Convert.ToBase64String(hashBytes, 0, hashBytes.Length / 2);
                hashString = hashString.Split('=')[0]; // Remove any trailing padding
                hashString = hashString.Replace('+', '-'); // 62nd char of encoding
                return hashString.Replace('/', '_'); // 63rd char of encoding
            }
        }

        private string GenerateKey(int length) {
            var bytes = new byte[length];
            Options.RandomNumberGenerator.GetBytes(bytes);
            return Convert.ToBase64String(bytes);
        }

        private async Task<string> CreateAuthorizationCodeAsync(AuthenticationTicket ticket, OpenIdConnectMessage request, OpenIdConnectMessage response) {
            // Create a copy to avoid modifying the original properties and compute
            // the expiration date using the registered authorization code lifetime.
            var properties = ticket.Properties.Copy() ?? new AuthenticationProperties();
            properties.IssuedUtc = Options.SystemClock.UtcNow;
            properties.ExpiresUtc = properties.IssuedUtc.Value + Options.AuthorizationCodeLifetime;
            ticket = new AuthenticationTicket(ticket.Identity, properties);

            var notification = new CreateAuthorizationCodeNotification(Context, Options, ticket);
            await Options.Provider.CreateAuthorizationCode(notification);

            // Skip the default logic if HandledResponse has been called.
            if (notification.HandledResponse) {
                return notification.AuthorizationCode;
            }

            if (!Options.TokenEndpointPath.HasValue) {
                throw new InvalidOperationException(
                    "An authorization code cannot be created " +
                    "when the token endpoint has been explicitly disabled.");
            }

            // Claims in authorization codes are never filtered as they are supposed to be opaque:
            // CreateAccessTokenAsync and CreateIdentityTokenAsync are responsible of ensuring
            // that subsequent access and identity tokens are correctly filtered.
            var content = Options.AuthorizationCodeFormat.Protect(ticket);

            var key = GenerateKey(256 / 8);
            Options.Cache.Set(key, content, properties.ExpiresUtc.Value);

            return key;
        }

        private async Task<string> CreateAccessTokenAsync(AuthenticationTicket ticket, OpenIdConnectMessage request, OpenIdConnectMessage response) {
            // Create a copy to avoid modifying the original properties and compute
            // the expiration date using the registered access token lifetime.
            var properties = ticket.Properties.Copy() ?? new AuthenticationProperties();
            properties.IssuedUtc = Options.SystemClock.UtcNow;
            properties.ExpiresUtc = properties.IssuedUtc.Value + Options.AccessTokenLifetime;
            ticket = new AuthenticationTicket(ticket.Identity, properties);

            var notification = new CreateAccessTokenNotification(Context, Options, ticket);
            await Options.Provider.CreateAccessToken(notification);

            // Allow the application to change the authentication
            // ticket from the CreateAccessTokenAsync notification.
            ticket = notification.AuthenticationTicket;

            // Skip the default logic if HandledResponse has been called.
            if (notification.HandledResponse) {
                return notification.AccessToken;
            }

            // Create a new identity containing only the filtered claims.
            // Actors identities are also filtered (delegation scenarios).
            var identity = ticket.Identity.Clone(claim => {
                // ClaimTypes.NameIdentifier and JwtRegisteredClaimNames.Sub are never excluded.
                if (string.Equals(claim.Type, ClaimTypes.NameIdentifier, StringComparison.Ordinal) ||
                    string.Equals(claim.Type, JwtRegisteredClaimNames.Sub, StringComparison.Ordinal)) {
                    return true;
                }

                if (Options.AccessTokenHandler != null && Options.EncryptingCredentials == null) {
                    // When a security token handler is used without encryption credentials, claims whose
                    // destination is not explicitly referenced are not included in the access token.
                    // Claims whose destination doesn't contain "token" are excluded.
                    return claim.HasDestination(OpenIdConnectConstants.ResponseTypes.Token);
                }

                // By default, claims whose destination is not referenced
                // are included in the access tokens when a data protector is used.
                // Claims whose destination doesn't contain "token" are excluded.
                return !claim.HasDestination() || claim.HasDestination(OpenIdConnectConstants.ResponseTypes.Token);
            });

            if (Options.AccessTokenHandler == null) {
                // Replace the authentication ticket by a new one
                // exposing the properties and the filtered identity.
                ticket = new AuthenticationTicket(identity, properties);

                return Options.AccessTokenFormat.Protect(ticket);
            }

            var token = Options.AccessTokenHandler.CreateToken(new SecurityTokenDescriptor {
                Subject = identity,
                AppliesToAddress = request.Resource,
                TokenIssuerName = Options.Issuer + "/",
                EncryptingCredentials = Options.EncryptingCredentials,
                SigningCredentials = Options.SigningCredentials,
                Lifetime = new Lifetime(
                    properties.IssuedUtc.Value.UtcDateTime,
                    properties.ExpiresUtc.Value.UtcDateTime)
            });

            return Options.AccessTokenHandler.WriteToken(token);
        }

        private async Task<string> CreateRefreshTokenAsync(AuthenticationTicket ticket, OpenIdConnectMessage request, OpenIdConnectMessage response) {
            // Create a copy to avoid modifying the original properties and compute
            // the expiration date using the registered refresh token lifetime.
            var properties = ticket.Properties.Copy() ?? new AuthenticationProperties();
            properties.IssuedUtc = Options.SystemClock.UtcNow;
            properties.ExpiresUtc = properties.IssuedUtc.Value + Options.RefreshTokenLifetime;
            ticket = new AuthenticationTicket(ticket.Identity, properties);

            var notification = new CreateRefreshTokenNotification(Context, Options, ticket);
            await Options.Provider.CreateRefreshToken(notification);

            // Allow the application to change the authentication
            // ticket from the CreateRefreshTokenAsync notification.
            ticket = notification.AuthenticationTicket;

            // Skip the default logic if HandledResponse has been called.
            if (notification.HandledResponse) {
                return notification.RefreshToken;
            }
            
            // Claims in refresh tokens are never filtered as they are supposed to be opaque:
            // CreateAccessTokenAsync and CreateIdentityTokenAsync are responsible of ensuring
            // that subsequent access and identity tokens are correctly filtered.
            return Options.RefreshTokenFormat.Protect(ticket);
        }

        private async Task<string> CreateIdentityTokenAsync(AuthenticationTicket ticket, OpenIdConnectMessage request, OpenIdConnectMessage response) {
            // Create a copy to avoid modifying the original properties and compute
            // the expiration date using the registered identity token lifetime.
            var properties = ticket.Properties.Copy() ?? new AuthenticationProperties();
            properties.IssuedUtc = Options.SystemClock.UtcNow;
            properties.ExpiresUtc = properties.IssuedUtc.Value + Options.IdentityTokenLifetime;
            ticket = new AuthenticationTicket(ticket.Identity, properties);

            var notification = new CreateIdentityTokenNotification(Context, Options, ticket);
            await Options.Provider.CreateIdentityToken(notification);

            // Allow the application to change the authentication
            // ticket from the CreateIdentityTokenAsync notification.
            ticket = notification.AuthenticationTicket;

            // Skip the default logic if HandledResponse has been called.
            if (notification.HandledResponse) {
                return notification.IdentityToken;
            }

            // Replace the identity by a new one containing only the filtered claims.
            // Actors identities are also filtered (delegation scenarios).
            var identity = ticket.Identity.Clone(claim => {
                // ClaimTypes.NameIdentifier and JwtRegisteredClaimNames.Sub are never excluded.
                if (string.Equals(claim.Type, ClaimTypes.NameIdentifier, StringComparison.Ordinal) ||
                    string.Equals(claim.Type, JwtRegisteredClaimNames.Sub, StringComparison.Ordinal)) {
                    return true;
                }

                // By default, claims whose destination is not
                // referenced are not included in the identity token.
                // Claims whose destination doesn't contain "id_token" are excluded.
                return claim.HasDestination(OpenIdConnectConstants.ResponseTypes.IdToken);
            });

            identity.AddClaim(JwtRegisteredClaimNames.Iat, EpochTime.GetIntDate(properties.IssuedUtc.Value.UtcDateTime).ToString());

            if (!string.IsNullOrEmpty(response.Code)) {
                identity.AddClaim(JwtRegisteredClaimNames.CHash, GenerateHash(response.Code, Options.SigningCredentials.DigestAlgorithm));
            }

            if (!string.IsNullOrEmpty(response.AccessToken)) {
                identity.AddClaim("at_hash", GenerateHash(response.AccessToken, Options.SigningCredentials.DigestAlgorithm));
            }

            if (!string.IsNullOrEmpty(request.Nonce)) {
                identity.AddClaim(JwtRegisteredClaimNames.Nonce, request.Nonce);
            }

            // While the 'sub' claim is declared mandatory by the OIDC specs,
            // it is not always issued as-is by the authorization servers.
            // When absent, the name identifier claim is used as a substitute.
            // See http://openid.net/specs/openid-connect-core-1_0.html#IDToken
            var subject = ticket.Identity.FindFirst(JwtRegisteredClaimNames.Sub);
            if (subject == null) {
                var identifier = ticket.Identity.FindFirst(ClaimTypes.NameIdentifier);
                if (identifier == null) {
                    throw new InvalidOperationException(
                        "A unique identifier cannot be found to generate a 'sub' claim. " +
                        "Make sure to either add a 'sub' or a 'ClaimTypes.NameIdentifier' claim " +
                        "in the returned ClaimsIdentity before calling SignIn.");
                }

                identity.AddClaim(JwtRegisteredClaimNames.Sub, identifier.Value);
            }

            if (Options.IdentityTokenHandler == null) {
                throw new InvalidOperationException(
                    "A security token handler is required to create an identity token: " +
                    "make sure to assign a valid instance to Options.IdentityTokenHandler " +
                    "or to override the Options.Provider.CreateIdentityToken " +
                    "notification and provide a custom identity token.");
            }

            if (Options.SigningCredentials == null) {
                throw new InvalidOperationException(
                    "Signing credentials are required to create an identity token: " +
                    "make sure to assign a valid instance to Options.SigningCredentials.");
            }

            var token = Options.IdentityTokenHandler.CreateToken(
                subject: identity,
                issuer: Options.Issuer + "/",
                signingCredentials: Options.SigningCredentials,
                audience: request.ClientId,
                notBefore: properties.IssuedUtc.Value.UtcDateTime,
                expires: properties.ExpiresUtc.Value.UtcDateTime);

            return Options.IdentityTokenHandler.WriteToken(token);
        }

        private async Task<AuthenticationTicket> ReceiveAuthorizationCodeAsync(string code) {
            var notification = new ReceiveAuthorizationCodeNotification(Context, Options, code);
            await Options.Provider.ReceiveAuthorizationCode(notification);
            
            // Directly return the authentication ticket if one
            // has been provided by ReceiveAuthorizationCode.
            var ticket = notification.AuthenticationTicket;
            if (ticket != null) {
                return ticket;
            }

            var payload = (string) Options.Cache.Get(code);
            if (payload == null) {
                return null;
            }

            // Because authorization codes are guaranteed to be unique, make sure
            // to remove the current code from the global store before using it.
            Options.Cache.Remove(code);

            return Options.AuthorizationCodeFormat.Unprotect(payload);
        }

        private async Task<AuthenticationTicket> ReceiveRefreshTokenAsync(string token) {
            var notification = new ReceiveRefreshTokenNotification(Context, Options, token);
            await Options.Provider.ReceiveRefreshToken(notification);

            // Directly return the authentication ticket if one
            // has been provided by ReceiveRefreshToken.
            var ticket = notification.AuthenticationTicket;
            if (ticket != null) {
                return ticket;
            }

            return Options.RefreshTokenFormat.Unprotect(token);
        }

        private static AuthenticationTicket ReturnOutcome(
            ValidateTokenRequestNotification validatingContext,
            BaseValidatingNotification<OpenIdConnectServerOptions> grantContext,
            AuthenticationTicket ticket,
            string defaultError) {
            if (!validatingContext.IsValidated) {
                return null;
            }

            if (!grantContext.IsValidated) {
                if (grantContext.HasError) {
                    validatingContext.SetError(
                        grantContext.Error,
                        grantContext.ErrorDescription,
                        grantContext.ErrorUri);
                }
                else {
                    validatingContext.SetError(defaultError);
                }
                return null;
            }

            if (ticket == null) {
                validatingContext.SetError(defaultError);
                return null;
            }

            return ticket;
        }

        private async Task<bool> SendErrorRedirectAsync(OpenIdConnectMessage request, OpenIdConnectMessage response) {
            // Remove the authorization request from the OWIN context to inform
            // TeardownCoreAsync that there's nothing more to handle.
            Context.SetOpenIdConnectRequest(request: null);

            // Directly display an error page if redirect_uri cannot be used.
            if (string.IsNullOrWhiteSpace(response.RedirectUri)) {
                return await SendErrorPageAsync(response);
            }

            // Try redirecting the user agent to the client
            // application or display a default error page.
            if (!await ApplyAuthorizationResponseAsync(request, response)) {
                return await SendErrorPageAsync(response);
            }

            // Stop processing the request.
            return true;
        }

        private async Task<bool> SendErrorPageAsync(OpenIdConnectMessage response) {
            if (Options.ApplicationCanDisplayErrors) {
                Context.SetOpenIdConnectResponse(response);

                // Request is not handled - pass through to application for rendering.
                return false;
            }

            using (var buffer = new MemoryStream())
            using (var writer = new StreamWriter(buffer)) {
                writer.WriteLine("error: {0}", response.Error);

                if (!string.IsNullOrEmpty(response.ErrorDescription)) {
                    writer.WriteLine("error_description: {0}", response.ErrorDescription);
                }

                if (!string.IsNullOrEmpty(response.ErrorUri)) {
                    writer.WriteLine("error_uri: {0}", response.ErrorUri);
                }

                writer.Flush();

                Response.StatusCode = 400;
                Response.ContentLength = buffer.Length;
                Response.ContentType = "text/plain;charset=UTF-8";

                Response.Headers.Set("Cache-Control", "no-cache");
                Response.Headers.Set("Pragma", "no-cache");
                Response.Headers.Set("Expires", "-1");

                buffer.Seek(offset: 0, loc: SeekOrigin.Begin);
                await buffer.CopyToAsync(Response.Body, 4096, Request.CallCancelled);

                return true;
            }
        }

        private async Task SendErrorPayloadAsync(OpenIdConnectMessage response) {
            using (var buffer = new MemoryStream())
            using (var writer = new JsonTextWriter(new StreamWriter(buffer))) {
                var payload = new JObject();

                payload.Add(OpenIdConnectConstants.Parameters.Error, response.Error);

                if (!string.IsNullOrEmpty(response.ErrorDescription)) {
                    payload.Add(OpenIdConnectConstants.Parameters.ErrorDescription, response.ErrorDescription);
                }

                if (!string.IsNullOrEmpty(response.ErrorUri)) {
                    payload.Add(OpenIdConnectConstants.Parameters.ErrorUri, response.ErrorUri);
                }

                payload.WriteTo(writer);
                writer.Flush();

                Response.StatusCode = 400;
                Response.ContentLength = buffer.Length;
                Response.ContentType = "application/json;charset=UTF-8";

                Response.Headers.Set("Cache-Control", "no-cache");
                Response.Headers.Set("Pragma", "no-cache");
                Response.Headers.Set("Expires", "-1");

                buffer.Seek(offset: 0, loc: SeekOrigin.Begin);
                await buffer.CopyToAsync(Response.Body, 4096, Request.CallCancelled);
            }
        }

        private class Appender {
            private readonly char _delimiter;
            private readonly StringBuilder _sb;
            private bool _hasDelimiter;

            public Appender(string value, char delimiter) {
                _sb = new StringBuilder(value);
                _delimiter = delimiter;
                _hasDelimiter = value.IndexOf(delimiter) != -1;
            }

            public Appender Append(string name, string value) {
                _sb.Append(_hasDelimiter ? '&' : _delimiter)
                   .Append(Uri.EscapeDataString(name))
                   .Append('=')
                   .Append(Uri.EscapeDataString(value));
                _hasDelimiter = true;
                return this;
            }

            public override string ToString() {
                return _sb.ToString();
            }
        }
    }
}
