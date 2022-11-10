using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using PluginBuilder;
using PluginBuilder.Services;

namespace PluginBuilder.Components.MainNav
{
    public class MainNav : ViewComponent
    {
        public MainNav(DBConnectionFactory connectionFactory)
        {
            ConnectionFactory = connectionFactory;
        }

        public DBConnectionFactory ConnectionFactory { get; }

        public async Task<IViewComponentResult> InvokeAsync()
        {
            var pluginSlug = ViewContext.HttpContext.GetPluginSlug();
            var vm = new MainNavViewModel { PluginSlug = pluginSlug?.ToString() };
            if (pluginSlug != null)
            {
                using var conn = await ConnectionFactory.Open();
                var rows = await conn.QueryAsync<int[]>("SELECT ver FROM versions WHERE plugin_slug=@pluginSlug ORDER BY ver DESC LIMIT 10", new { pluginSlug = pluginSlug.ToString() });
                foreach (var r in rows)
                    vm.Versions.Add(new PluginVersion(r).Version);
            }
            return View(vm);
        }

    }
}
