﻿/*
 * Licensed under the Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0)
 * See https://github.com/AspNet-OpenIdConnect-Server/Owin.Security.OpenIdConnect.Server
 * for more information concerning the license and the contributors participating to this project.
 */

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IdentityModel.Tokens;
using System.IO;
using System.Linq;
using System.Security.Claims;
using Microsoft.IdentityModel.Protocols;
using Microsoft.Owin;
using Microsoft.Owin.Security;
using Microsoft.Owin.Security.DataHandler;
using Microsoft.Owin.Security.DataHandler.Encoder;
using Microsoft.Owin.Security.DataHandler.Serializer;
using Microsoft.Owin.Security.DataProtection;
using Owin.Security.OpenIdConnect.Server;

namespace Owin {
    /// <summary>
    /// Provides extension methods allowing to easily register an
    /// OWIN-powered OpenID Connect server and to retrieve various
    /// OpenID Connect-related contexts from the OWIN environment.
    /// </summary>
    public static class OpenIdConnectServerExtensions {
        /// <summary>
        /// Adds a specs-compliant OpenID Connect server in the OWIN pipeline.
        /// </summary>
        /// <param name="app">The web application builder</param>
        /// <param name="options">Options which control the behavior of the OpenID Connect server.</param>
        /// <returns>The application builder</returns>
        public static IAppBuilder UseOpenIdConnectServer(this IAppBuilder app, OpenIdConnectServerOptions options) {
            if (app == null) {
                throw new ArgumentNullException("app");
            }

            if (options == null) {
                throw new ArgumentNullException("options");
            }

            return app.Use(typeof(OpenIdConnectServerMiddleware), app, options);
        }

        /// <summary>
        /// Retrieves the <see cref="OpenIdConnectMessage"/> instance
        /// associated with the current request from the OWIN context.
        /// </summary>
        /// <param name="context">The OWIN context.</param>
        /// <returns>The <see cref="OpenIdConnectMessage"/> associated with the current request.</returns>
        public static OpenIdConnectMessage GetOpenIdConnectRequest(this IOwinContext context) {
            if (context == null) {
                throw new ArgumentNullException("context");
            }

            return context.GetOpenIdConnectMessage(OpenIdConnectConstants.Environment.Request);
        }

        /// <summary>
        /// Inserts the ambient <see cref="OpenIdConnectMessage"/> request in the OWIN context.
        /// </summary>
        /// <param name="context">The OWIN context.</param>
        /// <param name="request">The ambient <see cref="OpenIdConnectMessage"/>.</param>
        public static void SetOpenIdConnectRequest(this IOwinContext context, OpenIdConnectMessage request) {
            if (context == null) {
                throw new ArgumentNullException("context");
            }

            context.SetOpenIdConnectMessage(OpenIdConnectConstants.Environment.Request, request);
        }

        /// <summary>
        /// Retrieves the <see cref="OpenIdConnectMessage"/> instance
        /// associated with the current response from the OWIN context.
        /// </summary>
        /// <param name="context">The OWIN context.</param>
        /// <returns>The <see cref="OpenIdConnectMessage"/> associated with the current response.</returns>
        public static OpenIdConnectMessage GetOpenIdConnectResponse(this IOwinContext context) {
            if (context == null) {
                throw new ArgumentNullException("context");
            }

            return context.GetOpenIdConnectMessage(OpenIdConnectConstants.Environment.Response);
        }

        /// <summary>
        /// Inserts the ambient <see cref="OpenIdConnectMessage"/> response in the OWIN context.
        /// </summary>
        /// <param name="context">The OWIN context.</param>
        /// <param name="response">The ambient <see cref="OpenIdConnectMessage"/>.</param>
        public static void SetOpenIdConnectResponse(this IOwinContext context, OpenIdConnectMessage response) {
            if (context == null) {
                throw new ArgumentNullException("context");
            }

            context.SetOpenIdConnectMessage(OpenIdConnectConstants.Environment.Response, response);
        }

        /// <summary>
        /// Creates a new enhanced ticket format that supports serializing
        /// <see cref="ClaimsIdentity.Actor"/> and <see cref="Claim.Properties"/>.
        /// </summary>
        /// <param name="app">The web application builder</param>
        /// <param name="purposes">The unique values used to initialize the data protector.</param>
        public static ISecureDataFormat<AuthenticationTicket> CreateTicketFormat(this IAppBuilder app, params string[] purposes) {
            if (app == null) {
                throw new ArgumentNullException("app");
            }

            return new EnhancedTicketDataFormat(app.CreateDataProtector(purposes));
        }

        private static OpenIdConnectMessage GetOpenIdConnectMessage(this IOwinContext context, string key) {
            if (context == null) {
                throw new ArgumentNullException("context");
            }

            if (string.IsNullOrWhiteSpace(key)) {
                throw new ArgumentException("key");
            }

            var message = context.Get<OpenIdConnectMessage>(key + OpenIdConnectConstants.Environment.Message);
            if (message != null) {
                return message;
            }

            var parameters = context.Get<IReadOnlyDictionary<string, string[]>>(key + OpenIdConnectConstants.Environment.Parameters);
            if (parameters != null) {
                return new OpenIdConnectMessage(parameters);
            }

            return null;
        }

        private static void SetOpenIdConnectMessage(this IOwinContext context, string key, OpenIdConnectMessage message) {
            if (context == null) {
                throw new ArgumentNullException("context");
            }

            if (string.IsNullOrWhiteSpace(key)) {
                throw new ArgumentException("key");
            }

            if (message == null) {
                context.Environment.Remove(key + OpenIdConnectConstants.Environment.Message);
                context.Environment.Remove(key + OpenIdConnectConstants.Environment.Parameters);

                return;
            }

            var parameters = new ReadOnlyDictionary<string, string[]>(
                message.Parameters.ToDictionary(
                    keySelector: parameter => parameter.Key,
                    elementSelector: parameter => new[] { parameter.Value }));

            context.Set(key + OpenIdConnectConstants.Environment.Message, message);
            context.Set(key + OpenIdConnectConstants.Environment.Parameters, parameters);
        }

        internal static AuthenticationProperties Copy(this AuthenticationProperties properties) {
            if (properties == null) {
                return null;
            }

            return new AuthenticationProperties(properties.Dictionary.ToDictionary(pair => pair.Key, pair => pair.Value));
        }

        internal static string GetAudience(this AuthenticationProperties properties) {
            if (properties == null) {
                return null;
            }

            string audience;
            if (!properties.Dictionary.TryGetValue("audience", out audience)) {
                return null;
            }

            return audience;
        }

        // Remove when the built-in ticket serializer supports Claim.Properties.
        // See https://github.com/aspnet-contrib/AspNet.Security.OpenIdConnect.Server/issues/71
        private sealed class EnhancedTicketSerializer : IDataSerializer<AuthenticationTicket> {
            private const int FormatVersion = 3;

            public byte[] Serialize(AuthenticationTicket model) {
                if (model == null) {
                    throw new ArgumentNullException("model");
                }

                using (var buffer = new MemoryStream())
                using (var writer = new BinaryWriter(buffer)) {
                    writer.Write(FormatVersion);

                    WriteIdentity(writer, model.Identity);
                    PropertiesSerializer.Write(writer, model.Properties);

                    return buffer.ToArray();
                }
            }

            public AuthenticationTicket Deserialize(byte[] data) {
                if (data == null) {
                    throw new ArgumentNullException("data");
                }

                using (var buffer = new MemoryStream(data))
                using (var reader = new BinaryReader(buffer)) {
                    if (reader.ReadInt32() != FormatVersion) {
                        return null;
                    }

                    var identity = ReadIdentity(reader);
                    var properties = PropertiesSerializer.Read(reader);

                    return new AuthenticationTicket(identity, properties);
                }
            }
            
            private static void WriteIdentity(BinaryWriter writer, ClaimsIdentity identity) {
                writer.Write(identity.AuthenticationType);
                WriteWithDefault(writer, identity.NameClaimType, DefaultValues.NameClaimType);
                WriteWithDefault(writer, identity.RoleClaimType, DefaultValues.RoleClaimType);
                writer.Write(identity.Claims.Count());

                foreach (var claim in identity.Claims) {
                    WriteClaim(writer, claim, identity.NameClaimType);
                }

                var context = identity.BootstrapContext as BootstrapContext;
                if (context == null || string.IsNullOrWhiteSpace(context.Token)) {
                    writer.Write(0);
                }

                else {
                    writer.Write(context.Token.Length);
                    writer.Write(context.Token);
                }

                if (identity.Actor != null) {
                    writer.Write(true);
                    WriteIdentity(writer, identity.Actor);
                }

                else {
                    writer.Write(false);
                }
            }
            
            private static ClaimsIdentity ReadIdentity(BinaryReader reader) {
                var authenticationType = reader.ReadString();
                var nameClaimType = ReadWithDefault(reader, DefaultValues.NameClaimType);
                var roleClaimType = ReadWithDefault(reader, DefaultValues.RoleClaimType);
                var count = reader.ReadInt32();

                var claims = new Claim[count];

                for (int index = 0; index != count; ++index) {
                    claims[index] = ReadClaim(reader, nameClaimType);
                }

                var identity = new ClaimsIdentity(claims, authenticationType, nameClaimType, roleClaimType);

                int bootstrapContextSize = reader.ReadInt32();
                if (bootstrapContextSize > 0) {
                    identity.BootstrapContext = new BootstrapContext(reader.ReadString());
                }

                if (reader.ReadBoolean()) {
                    identity.Actor = ReadIdentity(reader);
                }

                return identity;
            }

            private static void WriteClaim(BinaryWriter writer, Claim claim, string nameClaimType) {
                WriteWithDefault(writer, claim.Type, nameClaimType);
                writer.Write(claim.Value);
                WriteWithDefault(writer, claim.ValueType, DefaultValues.StringValueType);
                WriteWithDefault(writer, claim.Issuer, DefaultValues.LocalAuthority);
                WriteWithDefault(writer, claim.OriginalIssuer, claim.Issuer);
                writer.Write(claim.Properties.Count);

                foreach (var property in claim.Properties) {
                    writer.Write(property.Key);
                    writer.Write(property.Value);
                }
            }

            private static Claim ReadClaim(BinaryReader reader, string nameClaimType) {
                var type = ReadWithDefault(reader, nameClaimType);
                var value = reader.ReadString();
                var valueType = ReadWithDefault(reader, DefaultValues.StringValueType);
                var issuer = ReadWithDefault(reader, DefaultValues.LocalAuthority);
                var originalIssuer = ReadWithDefault(reader, issuer);
                var count = reader.ReadInt32();

                var claim = new Claim(type, value, valueType, issuer, originalIssuer);

                for (var index = 0; index != count; ++index) {
                    claim.Properties.Add(key: reader.ReadString(), value: reader.ReadString());
                }

                return claim;
            }

            private static void WriteWithDefault(BinaryWriter writer, string value, string defaultValue) {
                if (string.Equals(value, defaultValue, StringComparison.Ordinal)) {
                    writer.Write(DefaultValues.DefaultStringPlaceholder);
                }

                else {
                    writer.Write(value);
                }
            }

            private static string ReadWithDefault(BinaryReader reader, string defaultValue) {
                string value = reader.ReadString();
                if (string.Equals(value, DefaultValues.DefaultStringPlaceholder, StringComparison.Ordinal)) {
                    return defaultValue;
                }

                return value;
            }

            private static class DefaultValues {
                public const string DefaultStringPlaceholder = "\0";
                public const string NameClaimType = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name";
                public const string RoleClaimType = "http://schemas.microsoft.com/ws/2008/06/identity/claims/role";
                public const string LocalAuthority = "LOCAL AUTHORITY";
                public const string StringValueType = "http://www.w3.org/2001/XMLSchema#string";
            }
        }

        private sealed class EnhancedTicketDataFormat : SecureDataFormat<AuthenticationTicket> {
            private static readonly EnhancedTicketSerializer Serializer = new EnhancedTicketSerializer();

            public EnhancedTicketDataFormat(IDataProtector protector)
                : base(Serializer, protector, TextEncodings.Base64Url) {
            }
        }
    }
}
