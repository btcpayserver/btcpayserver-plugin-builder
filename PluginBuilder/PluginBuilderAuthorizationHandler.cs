using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using PluginBuilder.Services;

namespace PluginBuilder
{
    public class PluginBuilderAuthorizationHandler : AuthorizationHandler<OwnPluginRequirement>
    {
        public PluginBuilderAuthorizationHandler(
            DBConnectionFactory connectionFactory,
            UserManager<IdentityUser> userManager)
        {
            ConnectionFactory = connectionFactory;
            UserManager = userManager;
        }

        public DBConnectionFactory ConnectionFactory { get; }
        public UserManager<IdentityUser> UserManager { get; }

        protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, OwnPluginRequirement requirement)
        {
            var httpContext = context.Resource as HttpContext;
            object? v = null;
            PluginSlug? slug = context.Resource as PluginSlug;
            if (slug is null)
            {
                if (httpContext?.GetRouteData().Values.TryGetValue("pluginSlug", out v) is not true)
                {
                    return;
                }
                if (v is not string v2 || !PluginSlug.TryParse(v2, out slug))
                {
                    context.Fail();
                    return;
                }
            }
            using var conn = await ConnectionFactory.Open();
            if (await conn.UserOwnsPlugin(UserManager.GetUserId(context.User), slug))
            {
                context.Succeed(requirement);
                httpContext?.SetPluginSlug(slug);
            }
        }
    }
}
