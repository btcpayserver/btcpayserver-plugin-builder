using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using PluginBuilder.Services;

namespace PluginBuilder.Components.PluginSelector
{
    public class PluginSelector : ViewComponent
    {
        private readonly UserManager<IdentityUser> _userManager;

        public PluginSelector(
            DBConnectionFactory connectionFactory,
            UserManager<IdentityUser> userManager)
        {
            ConnectionFactory = connectionFactory;
            _userManager = userManager;
        }

        public DBConnectionFactory ConnectionFactory { get; }

        public async Task<IViewComponentResult> InvokeAsync()
        {
            using var connection = await ConnectionFactory.Open();
            var userId = _userManager.GetUserId(UserClaimsPrincipal)!;
            var plugins = await connection.GetPluginsByUserId(userId);

            PluginSlug? currentPluginSlug = HttpContext.GetPluginSlug();
            var options = plugins
                .Select(pluginSlug =>
                new PluginSelectorOption
                {
                    Text = pluginSlug.ToString(),
                    Value = pluginSlug.ToString(),
                    Selected = pluginSlug == currentPluginSlug,
                    PluginSlug = pluginSlug
                })
                .OrderBy(s => s.Text)
                .ToList();

            var vm = new PluginSelectorViewModel
            {
                Options = options,
                PluginSlug = currentPluginSlug
            };

            return View(vm);
        }
    }
}
