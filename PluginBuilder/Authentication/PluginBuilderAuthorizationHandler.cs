using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using PluginBuilder.Extensions;
using PluginBuilder.Services;

namespace PluginBuilder;

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
        var slug = context.Resource as PluginSlug;
        if (slug is null)
        {
            if (httpContext?.GetRouteData().Values.TryGetValue("pluginSlug", out v) is not true) return;
            if (v is not string v2 || !PluginSelectorBySlug.TryParse(v2, out var slugSelector))
            {
                context.Fail();
                return;
            }

            slug = await ConnectionFactory.ResolvePluginSlug(slugSelector);
        }

        if (slug is null)
        {
            context.Fail();
            return;
        }

        await using var conn = await ConnectionFactory.Open();
        var userId = UserManager.GetUserId(context.User)!;
        if (await conn.UserOwnsPlugin(userId, slug))
        {
            context.Succeed(requirement);
            httpContext?.SetPluginSlug(slug);
        }
    }
}
