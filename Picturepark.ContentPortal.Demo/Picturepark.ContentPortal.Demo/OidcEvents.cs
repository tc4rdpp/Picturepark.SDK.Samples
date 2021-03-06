using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Web;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Newtonsoft.Json;
using Picturepark.ContentPortal.Demo.Contract;
using Picturepark.ContentPortal.Demo.Controllers;
using Picturepark.SDK.V1.Contract;

namespace Picturepark.ContentPortal.Demo
{
    public class OidcEvents : OpenIdConnectEvents
    {
        private readonly IPictureparkService _pictureparkClient;
        private readonly PictureparkConfiguration _cpConfig;

        public OidcEvents(IPictureparkService pictureparkClient, PictureparkConfiguration cpConfig)
        {
            _pictureparkClient = pictureparkClient;
            _cpConfig = cpConfig;
        }

        /// <summary>
        /// Adds all the information needed for communication with IdentityServer
        /// </summary>
        /// <param name="context"></param>
        /// <returns></returns>
        public override Task RedirectToIdentityProvider(RedirectContext context)
        {
            var tenant = new { id = _cpConfig.CustomerId, alias = _cpConfig.CustomerAlias };
            context.ProtocolMessage.SetParameter("acr_values", "tenant:" + JsonConvert.SerializeObject(tenant));

            var localUrl = context.Properties.Items["localUrl"];
            context.ProtocolMessage.SetParameter("cp_base_uri", _cpConfig.FrontendBaseUrl);
            context.ProtocolMessage.SetParameter("register_uri", BuildRegisterRedirectUri(localUrl));
            context.ProtocolMessage.SetParameter("terms_uri", BuildTermsOfServiceUri(_cpConfig));

            return Task.CompletedTask;
        }

        private static string BuildTermsOfServiceUri(PictureparkConfiguration cpConfig)
            => new Uri(new Uri(cpConfig.ApplicationBaseUrl), "/service/terms/newest").AbsoluteUri;

        private static string BuildRegisterRedirectUri(string localUrl)
        {
            var query = HttpUtility.ParseQueryString(string.Empty);
            query["redirect"] = $"{localUrl}{AccountController.LoginPath}";

            return $"/terms?{query}";
        }

        /// <summary>
        /// Checks if the user is reviewed and assigns the automatic user roles
        /// </summary>
        /// <param name="context"></param>
        /// <returns></returns>
        public override async Task TokenValidated(TokenValidatedContext context)
        {
            var user = await GetUserDetails(context);

            await EnsureUserIsReviewed(user);

            await EnsureUserRoles(user);
        }

        private async Task EnsureUserRoles(UserDetail user)
        {
            user.UserRoles = user.UserRoles ?? new List<UserRole>();

            var toAssign = await GetAutoUserRoleIds();

            foreach (var userRole in toAssign.Select(id => new UserRole {Id = id}))
            {
                if (user.UserRoles.All(ur => ur.Id != userRole.Id))
                    user.UserRoles.Add(userRole);
            }

            await _pictureparkClient.User.UpdateAsync(user.Id, user);
        }

        private async Task EnsureUserIsReviewed(UserDetail user)
        {
            if (user.AuthorizationState == AuthorizationState.ToBeReviewed)
                await _pictureparkClient.User.ReviewAsync(user.Id, new UserReviewRequest {Reviewed = true});
        }

        private async Task<UserDetail> GetUserDetails(TokenValidatedContext context)
        {
            var email = context.Principal.Identity.Name;

            var results = await _pictureparkClient.User.SearchAsync(new UserSearchRequest
            {
                Filter = FilterBase.FromExpression<User>(u => u.EmailAddress, email)
            });

            if (results.Results.Count != 1)
                throw new Exception("Unable to find the logged-in user in CP");

            var userId = results.Results.Single().Id;

            return await _pictureparkClient.User.GetAsync(userId);
        }

        private async Task<IReadOnlyList<string>> GetAutoUserRoleIds()
        {
            var userRoles = _cpConfig.AutoAssignUserRoleIds;

            var checkRoleTasks = userRoles.Select(async r =>
            {
                var role = await _pictureparkClient.UserRole.GetAsync(r);
                if (role == null)
                    throw new Exception($"Unable to find user role with id '{r}'");
            });

            await Task.WhenAll(checkRoleTasks);

            return userRoles;
        }
    }
}