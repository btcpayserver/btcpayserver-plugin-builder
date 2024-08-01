using System.Reflection;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PluginBuilder.APIModels;
using PluginBuilder.Authentication;
using PluginBuilder.ModelBinders;
using PluginBuilder.Services;

namespace PluginBuilder.Controllers;

[ApiController]
[Route("~/api/v1")]
[Authorize(Policy = Policies.OwnPlugin, AuthenticationSchemes = PluginBuilderAuthenticationSchemes.BasicAuth)]
public class ApiController : ControllerBase
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
    [HttpGet("version")]
    public IActionResult GetVersion()
    {
        return Ok(new JObject
        {
            ["version"] = typeof(HomeController).GetTypeInfo().Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
            ["commit"] = typeof(HomeController).GetTypeInfo().Assembly.GetCustomAttribute<GitCommitAttribute>()?.SHA
        });
    }

    [AllowAnonymous]
    [HttpGet("plugins")]
    public async Task<IActionResult> Plugins(
        [ModelBinder(typeof(PluginVersionModelBinder))] PluginVersion? btcpayVersion = null,
        bool? includePreRelease = null, bool? includeAllVersions = null)
    {
        includePreRelease ??= false;
        includeAllVersions ??= false;
        var getVersions = includeAllVersions switch
        {
            true => "get_all_versions",
            false => "get_latest_versions"
        };
        await using var conn = await ConnectionFactory.Open();
        // This query probably doesn't have right indexes
        var rows = await conn.QueryAsync<(string plugin_slug, int[] ver, string settings, long id, string manifest_info, string build_info)>(
            $"SELECT lv.plugin_slug, lv.ver, p.settings, b.id, b.manifest_info, b.build_info FROM {getVersions}(@btcpayVersion, @includePreRelease) lv " +
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
                Version = string.Join('.', r.ver),
                BuildId = r.id,
                BuildInfo = JObject.Parse(r.build_info),
                ManifestInfo = JObject.Parse(r.manifest_info),
                PluginLogo = JsonConvert.DeserializeObject<PluginSettings>(r.settings)!.Logo,
                Documentation = JsonConvert.DeserializeObject<PluginSettings>(r.settings)!.Documentation
            };
            versions.Add(v);
        }
        return Ok(versions);
    }

    [AllowAnonymous]
    [HttpGet("plugins/{pluginSlug}/versions/{version}/download")]
    public async Task<IActionResult> Download(
        [ModelBinder(typeof(PluginSlugModelBinder))] PluginSlug pluginSlug,
        [ModelBinder(typeof(PluginVersionModelBinder))] PluginVersion version)
    {
        await using var conn = await ConnectionFactory.Open();
        var url = await conn.ExecuteScalarAsync<string?>(
            "SELECT b.build_info->>'url' FROM versions v " +
            "JOIN builds b ON b.plugin_slug = v.plugin_slug AND b.id = v.build_id " +
            "WHERE v.plugin_slug=@plugin_slug AND v.ver=@version",
            new
            {
                plugin_slug = pluginSlug.ToString(),
                version = version.VersionParts
            });
        if (url is null)
            return NotFound();

        await conn.InsertEvent("Download", new JObject
        {
            ["pluginSlug"] = pluginSlug.ToString(),
            ["version"] = version.ToString()
        });
        return Redirect(url);
    }

    [HttpPost("plugins/{pluginSlug}/builds")]
    public async Task<IActionResult> CreateBuild(
        [ModelBinder(typeof(PluginSlugModelBinder))] PluginSlug pluginSlug,
        CreateBuildRequest model)
    {
        await using var conn = await ConnectionFactory.Open();
        var settings = await conn.GetSettings(pluginSlug);

        // apply defaults from settings
        if (settings is not null)
        {
            model.GitRepository ??= settings.GitRepository;
            model.GitRef ??= settings.GitRef;
            model.PluginDirectory ??= settings.PluginDirectory;
            model.BuildConfig ??= settings.BuildConfig;
        }
        
        if (string.IsNullOrEmpty(model.GitRepository))
            ModelState.AddModelError(nameof(model.GitRepository), "Git repository is required");

        if (!ModelState.IsValid)
            return ValidationErrorResult(ModelState);

        var buildId = await conn.NewBuild(pluginSlug, model.ToBuildParameter());
        var buildUrl = Url.ActionLink(nameof(PluginController.Build), "Plugin",
            new { pluginSlug = pluginSlug.ToString(), buildId });
        
        _ = BuildService.Build(new FullBuildId(pluginSlug, buildId));
        
        return Ok(new JObject 
        {
            ["pluginSlug"] = pluginSlug.ToString(),
            ["buildId"] = buildId,
            ["buildUrl"] = buildUrl
        });
    }

    [HttpGet("plugins/{pluginSlug}/builds/{buildId}")]
    public async Task<IActionResult> Build(
        [ModelBinder(typeof(PluginSlugModelBinder))] PluginSlug pluginSlug,
        long buildId)
    {
        await using var conn = await ConnectionFactory.Open();
        var row = await conn.QueryFirstOrDefaultAsync<(string manifest_info, string build_info, DateTimeOffset created_at, bool published, bool pre_release)>(
            "SELECT manifest_info, build_info, created_at, v.ver IS NOT NULL, v.pre_release FROM builds b " +
            "LEFT JOIN versions v ON b.plugin_slug=v.plugin_slug AND b.id=v.build_id " +
            "WHERE b.plugin_slug=@pluginSlug AND id=@buildId " +
            "LIMIT 1",
            new { pluginSlug = pluginSlug.ToString(), buildId });
        
        var buildInfo = BuildInfo.Parse(row.build_info);
        var manifest = PluginManifest.Parse(row.manifest_info);
        var vm = new BuildData
        {
            BuildId = buildId,
            ProjectSlug = pluginSlug.ToString(),
            ManifestInfo = manifest,
            BuildInfo = buildInfo,
            CreatedDate = row.created_at,
            DownloadLink = buildInfo?.Url,
            Published = row.published,
            Prerelease = row.pre_release,
            Commit = buildInfo?.GitCommit?[..8],
            Repository = buildInfo?.GitRepository,
            GitRef = buildInfo?.GitRef
        };
        
        return Ok(vm);
    }

    private IActionResult ValidationErrorResult(ModelStateDictionary modelState)
    {
        var errors = (from error in modelState 
            from errorMessage in error.Value.Errors 
            select new ValidationError(error.Key, errorMessage.ErrorMessage)).ToList();

        return UnprocessableEntity(new { errors });
    }
}
