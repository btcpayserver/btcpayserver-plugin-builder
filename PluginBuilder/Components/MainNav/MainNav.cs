using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using PluginBuilder.Components.PluginVersion;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;

namespace PluginBuilder.Components.MainNav;

public class MainNav : ViewComponent
{
    public MainNav(DBConnectionFactory connectionFactory, UserManager<IdentityUser> userManager)
    {
        ConnectionFactory = connectionFactory;
        UserManager = userManager;
    }

    public DBConnectionFactory ConnectionFactory { get; }
    public UserManager<IdentityUser> UserManager { get; }

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var pluginSlug = ViewContext.HttpContext.GetPluginSlug();
        MainNavViewModel vm = new() { PluginSlug = pluginSlug?.ToString() };
        if (pluginSlug != null)
        {
            using var conn = await ConnectionFactory.Open();
            var rows = await conn.QueryAsync<(int[] ver, bool pre_release)>(
                "SELECT ver, pre_release FROM users_plugins up " +
                "JOIN versions v USING (plugin_slug) " +
                "WHERE up.user_id=@userId AND up.plugin_slug=@pluginSlug " +
                "ORDER BY v.ver DESC LIMIT 10", new { pluginSlug = pluginSlug.ToString(), userId = UserManager.GetUserId(UserClaimsPrincipal) });
            foreach (var r in rows)
                vm.Versions.Add(new PluginVersionViewModel
                {
                    PluginSlug = pluginSlug?.ToString(),
                    Version = new PluginBuilder.PluginVersion(r.ver).ToString(),
                    PreRelease = r.pre_release,
                    Published = true,
                    HidePublishBadge = true
                });
        }

        return View(vm);
    }
}
