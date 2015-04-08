/*
 * Licensed under the Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0)
 * See https://github.com/AspNet-OpenIdConnect-Server/Owin.Security.OpenIdConnect.Server
 * for more information concerning the license and the contributors participating to this project.
 */

using Microsoft.Owin;
using Microsoft.Owin.Security.Provider;
using Newtonsoft.Json.Linq;

namespace Owin.Security.OpenIdConnect.Server {
    /// <summary>
    /// An event raised before the authorization server starts
    /// writing the token validation to the response stream.
    /// </summary>
    public sealed class ValidationEndpointResponseNotification : EndpointContext<OpenIdConnectServerOptions> {
        /// <summary>
        /// Creates an instance of this context.
        /// </summary>
        internal ValidationEndpointResponseNotification(
            IOwinContext context,
            OpenIdConnectServerOptions options,
            JArray payload)
            : base(context, options) {
            Payload = payload;
        }

        /// <summary>
        /// Gets the JSON payload returned to the caller.
        /// </summary>
        public JArray Payload { get; private set; }
    }
}
