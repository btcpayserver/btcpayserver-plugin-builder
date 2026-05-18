using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using PluginBuilder.Components.PluginVersion;
using PluginBuilder.DataModels;
using PluginBuilder.Services;
using PluginBuilder.Util;
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

        using var conn = await ConnectionFactory.Open();

        if (pluginSlug is { } currentPluginSlug)
        {
            var slug = currentPluginSlug.ToString();
            var rows = await conn.QueryAsync<(int[] ver, bool pre_release)>(
                "SELECT ver, pre_release FROM users_plugins up " +
                "JOIN versions v USING (plugin_slug) " +
                "WHERE up.user_id=@userId AND up.plugin_slug=@pluginSlug " +
                "ORDER BY v.ver DESC LIMIT 10", new { pluginSlug = slug, userId = UserManager.GetUserId(UserClaimsPrincipal) });
            foreach (var r in rows)
                vm.Versions.Add(new PluginVersionViewModel
                {
                    PluginSlug = slug,
                    Version = new PluginBuilder.PluginVersion(r.ver).ToString(),
                    PreRelease = r.pre_release,
                    Published = true,
                    HidePublishBadge = true
                });

            var visibility = await conn.ExecuteScalarAsync<string?>(
                "SELECT visibility FROM plugins WHERE slug=@pluginSlug",
                new { pluginSlug = slug });
            vm.RequestListing = string.Equals(visibility, nameof(PluginVisibilityEnum.Unlisted).ToLowerInvariant(), StringComparison.Ordinal);
        }

        // Only load pending count for admins to avoid burdening database
        if (UserClaimsPrincipal.IsInRole(Roles.ServerAdmin))
            vm.PendingListingRequestsCount = await conn.GetPendingListingRequestsCount();

        return View(vm);
    }
}
