using System.Reflection;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PluginBuilder.APIModels;
using PluginBuilder.ModelBinders;
using PluginBuilder.Services;

namespace PluginBuilder.Controllers;

[Authorize]
public class ApiController : Controller
{
    private DBConnectionFactory ConnectionFactory { get; }
    public UserManager<IdentityUser> UserManager { get; }
    public RoleManager<IdentityRole> RoleManager { get; }
    public SignInManager<IdentityUser> SignInManager { get; }
    public IAuthorizationService AuthorizationService { get; }
    public BuildService BuildService { get; }
    public ServerEnvironment Env { get; }

    public ApiController(
        DBConnectionFactory connectionFactory,
        UserManager<IdentityUser> userManager,
        RoleManager<IdentityRole> roleManager,
        SignInManager<IdentityUser> signInManager,
        IAuthorizationService authorizationService,
        BuildService buildService,
        ServerEnvironment env)
    {
        ConnectionFactory = connectionFactory;
        BuildService = buildService;
        UserManager = userManager;
        RoleManager = roleManager;
        SignInManager = signInManager;
        AuthorizationService = authorizationService;
        Env = env;
    }
    
    [AllowAnonymous]
    [HttpGet("/api/v1/version")]
    public IActionResult GetVersion()
    {
        return Ok(new JObject
        {
            ["version"] = typeof(HomeController).GetTypeInfo().Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
            ["commit"] = typeof(HomeController).GetTypeInfo().Assembly.GetCustomAttribute<GitCommitAttribute>()?.SHA
        });
    }

    [AllowAnonymous]
    [HttpGet("/api/v1/plugins")]
    public async Task<IActionResult> Plugins(
        [ModelBinder(typeof(PluginVersionModelBinder))] PluginVersion? btcpayVersion = null,
        bool? includePreRelease = null)
    {
        includePreRelease ??= false;
        await using var conn = await ConnectionFactory.Open();
        // This query probably doesn't have right indexes
        var rows = await conn.QueryAsync<(string plugin_slug, string settings, long id, string manifest_info, string build_info)>(
            "SELECT lv.plugin_slug, p.settings, b.id, b.manifest_info, b.build_info FROM get_latest_versions(@btcpayVersion, @includePreRelease) lv " +
            "JOIN builds b ON b.plugin_slug = lv.plugin_slug AND b.id = lv.build_id " +
            "JOIN plugins p ON b.plugin_slug = p.slug " +
            "WHERE b.manifest_info IS NOT NULL AND b.build_info IS NOT NULL " +
            "ORDER BY manifest_info->>'Name'",
            new
            {
                btcpayVersion = btcpayVersion?.VersionParts,
                includePreRelease = includePreRelease.Value
            });
        rows.TryGetNonEnumeratedCount(out var count);
        var versions = new List<PublishedVersion>(count);
        foreach (var r in rows)
        {
            var v = new PublishedVersion
            {
                ProjectSlug = r.plugin_slug,
                BuildId = r.id,
                BuildInfo = JObject.Parse(r.build_info),
                ManifestInfo = JObject.Parse(r.manifest_info),
                Documentation = JsonConvert.DeserializeObject<PluginSettings>(r.settings)!.Documentation
            };
            versions.Add(v);
        }
        return Json(versions);
    }

    [AllowAnonymous]
    [HttpGet("/api/v1/plugins/{pluginSlug}/versions/{version}/download")]
    public async Task<IActionResult> Download(
        [ModelBinder(typeof(PluginSelectorModelBinder))] PluginSelector pluginSlug,
        [ModelBinder(typeof(PluginVersionModelBinder))] PluginVersion version)
    {
        await using var conn = await ConnectionFactory.Open();
        var slug = await conn.GetPluginSlug(pluginSlug);
        if (slug is null)
            return NotFound();
        
        var url = await conn.ExecuteScalarAsync<string?>(
            "SELECT b.build_info->>'url' FROM versions v " +
            "JOIN builds b ON b.plugin_slug = v.plugin_slug AND b.id = v.build_id " +
            "WHERE v.plugin_slug=@plugin_slug AND v.ver=@version",
            new
            {
                plugin_slug = slug.ToString(),
                version = version.VersionParts
            });
        if (url is null)
            return NotFound();

        await conn.InsertEvent("Download", new JObject
        {
            ["pluginSlug"] = slug.ToString(),
            ["version"] = version.ToString()
        });
        return Redirect(url);
    }
}
