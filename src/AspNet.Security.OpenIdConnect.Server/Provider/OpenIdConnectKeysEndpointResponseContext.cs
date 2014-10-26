/*
 * Licensed under the Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0)
 * See https://github.com/AspNet-OpenIdConnect-Server/Owin.Security.OpenIdConnect.Server
 * for more information concerning the license and the contributors participating to this project.
 */

using System.Collections.Generic;
using Microsoft.AspNet.Http;
using Microsoft.AspNet.Security.Notifications;
using Microsoft.IdentityModel.Protocols;

namespace AspNet.Security.OpenIdConnect.Server {
    /// <summary>
    /// An event raised before the authorization server starts
    /// writing the JWKS metadata to the response stream.
    /// </summary>
    public sealed class OpenIdConnectKeysEndpointResponseContext : EndpointContext<OpenIdConnectServerOptions> {
        /// <summary>
        /// Creates an instance of this context.
        /// </summary>
        internal OpenIdConnectKeysEndpointResponseContext(
            HttpContext context,
            OpenIdConnectServerOptions options)
            : base(context, options) {
            Keys = new List<JsonWebKey>();
        }

        /// <summary>
        /// Gets a list of the JSON Web Keys found by the authorization server.
        /// </summary>
        public IList<JsonWebKey> Keys { get; private set; }
    }
}
