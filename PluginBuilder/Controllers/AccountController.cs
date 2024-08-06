using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using PluginBuilder.APIModels;
using PluginBuilder.ModelBinders;
using PluginBuilder.Services;
using PluginBuilder.ViewModels;

namespace PluginBuilder.Controllers
{
    [Authorize]
    public class AccountController : Controller
    {
        private DBConnectionFactory ConnectionFactory { get; }
        private UserManager<IdentityUser> UserManager { get; }

        public AccountController(
            DBConnectionFactory connectionFactory,
            UserManager<IdentityUser> userManager)
        {
            ConnectionFactory = connectionFactory;
            UserManager = userManager;
        }

        [HttpGet("details")]
        public async Task<IActionResult> AccountDetails()
        {
            await using var conn = await ConnectionFactory.Open();
            var settings = await conn.GetAccountDetailSettings(UserManager.GetUserId(User)!);
            return View(settings);
        }


        [HttpGet("listplugins")]
        public async Task<IActionResult> ListPlugins(
        [ModelBinder(typeof(PluginVersionModelBinder))] PluginVersion? btcpayVersion = null)
        {
            await using var conn = await ConnectionFactory.Open();

            var rows = await conn.QueryAsync<(string plugin_slug, int[] ver, string settings, long id, string manifest_info, string build_info)>(
            $"SELECT lv.plugin_slug, lv.ver, p.settings, b.id, b.manifest_info, b.build_info FROM get_all_versions(@btcpayVersion, @includePreRelease) lv " +
            "JOIN builds b ON b.plugin_slug = lv.plugin_slug AND b.id = lv.build_id " +
            "JOIN plugins p ON b.plugin_slug = p.slug " +
            "WHERE b.manifest_info IS NOT NULL AND b.build_info IS NOT NULL " +
            "ORDER BY manifest_info->>'Name'",
            new
            {
                btcpayVersion = btcpayVersion?.VersionParts,
                includePreRelease = false
            });

            var versions = rows
                .Select(r => new PublishedVersion
                {
                    ProjectSlug = r.plugin_slug,
                    ManifestInfo = JObject.Parse(r.manifest_info),
                    Documentation = JsonConvert.DeserializeObject<PluginSettings>(r.settings)?.Documentation
                })
                .ToList();

            return View(versions);
        }


        [HttpGet("plugindetails/{pluginSlug}")]
        public async Task<IActionResult> PluginDetails(string pluginSlug,
        [ModelBinder(typeof(PluginVersionModelBinder))] PluginVersion? btcpayVersion = null)
        {
            await using var conn = await ConnectionFactory.Open();

            var row = await conn.QueryFirstOrDefaultAsync<(string plugin_slug, int[] ver, string settings, long id, string manifest_info, string build_info)>(
                $"SELECT lv.plugin_slug, lv.ver, p.settings, b.id, b.manifest_info, b.build_info FROM get_all_versions(@btcpayVersion, @includePreRelease) lv " +
                "JOIN builds b ON b.plugin_slug = lv.plugin_slug AND b.id = lv.build_id " +
                "JOIN plugins p ON b.plugin_slug = p.slug " +
                "WHERE b.manifest_info IS NOT NULL AND b.build_info IS NOT NULL AND lv.plugin_slug = @pluginSlug " +
                "ORDER BY manifest_info->>'Name'",
                new
                {
                    btcpayVersion = btcpayVersion?.VersionParts,
                    includePreRelease = false,
                    pluginSlug = pluginSlug
                });

            if (string.IsNullOrEmpty(row.plugin_slug))
            {
                return NotFound();
            }

            var plugin = new PublishedVersion
            {
                ProjectSlug = row.plugin_slug,
                Version = string.Join('.', row.ver),
                BuildId = row.id,
                BuildInfo = JObject.Parse(row.build_info),
                ManifestInfo = JObject.Parse(row.manifest_info),
                Documentation = JsonConvert.DeserializeObject<PluginSettings>(row.settings)!.Documentation
            };
            return View(plugin);
        }



        [HttpPost("details")]
        public async Task<IActionResult> AccountDetails(AccountSettings model)
        {
            if (!ModelState.IsValid)
            {
                return View(model);
            }

            await using var conn = await ConnectionFactory.Open();
            var user = UserManager.GetUserId(User)!;
            var accountSettings = await conn.GetAccountDetailSettings(user) ?? model;

            accountSettings.Nostr = model.Nostr ?? accountSettings.Nostr;
            accountSettings.Twitter = model.Twitter ?? accountSettings.Twitter;
            accountSettings.Github = model.Github ?? accountSettings.Github;
            accountSettings.Email = model.Email ?? accountSettings.Email;

            await conn.SetAccountDetailSettings(accountSettings, user);

            TempData[TempDataConstant.SuccessMessage] = "Account details updated successfully";
            return RedirectToAction(nameof(AccountDetails));
        }
    }
}
