﻿using Microsoft.AspNet.Http.Authentication;
using Microsoft.AspNet.Mvc;

namespace Mvc.Client.Controllers {
    public class AuthenticationController : Controller {
        [HttpGet("~/signin")]
        public ActionResult SignIn() {
            // Instruct the OIDC client middleware to redirect the user agent to the identity provider.
            // Note: the authenticationType parameter must match the value configured in Startup.cs
            return new ChallengeResult("OpenIdConnect", new AuthenticationProperties {
                RedirectUri = "/"
            });
        }

        [HttpGet("~/signout"), HttpPost("~/signout")]
        public ActionResult SignOut() {
            // Instruct the cookies middleware to delete the local cookie created when the user agent
            // is redirected from the identity provider after a successful authorization flow.
            // Note: this call doesn't disconnect the user agent at the identity provider level (yet).
            Context.Response.SignOut("ClientCookie");

            return Redirect("/");
        }
    }
}