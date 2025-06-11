using System.Reflection;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PluginBuilder.APIModels;
using PluginBuilder.Authentication;
using PluginBuilder.ModelBinders;
using PluginBuilder.Services;
using PluginBuilder.Util;
using PluginBuilder.Util.Extensions;

namespace PluginBuilder.Controllers;

[ApiController]
[Route("~/api/v1")]
[Authorize(Policy = Policies.OwnPlugin, AuthenticationSchemes = PluginBuilderAuthenticationSchemes.BasicAuth)]
public class ApiController(
    DBConnectionFactory connectionFactory,
    BuildService buildService)
    : ControllerBase
{
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
        [ModelBinder(typeof(PluginVersionModelBinder))]
        PluginVersion? btcpayVersion = null,
        bool? includePreRelease = null, bool? includeAllVersions = null, string? searchPluginName = null)
    {
        includePreRelease ??= false;
        includeAllVersions ??= false;
        var getVersions = includeAllVersions switch
        {
            true => "get_all_versions",
            false => "get_latest_versions"
        };
        await using var conn = await connectionFactory.Open();

        // This query definitely doesn't have right indexes
        var query = $"""
                     SELECT lv.plugin_slug, lv.ver, p.settings, b.id, b.manifest_info, b.build_info
                     FROM {getVersions}(@btcpayVersion, @includePreRelease) lv
                     JOIN builds b ON b.plugin_slug = lv.plugin_slug AND b.id = lv.build_id
                     JOIN plugins p ON b.plugin_slug = p.slug
                     WHERE b.manifest_info IS NOT NULL AND b.build_info IS NOT NULL 
                     AND (p.visibility = 'unlisted' OR p.visibility = 'listed')
                     {(!string.IsNullOrWhiteSpace(searchPluginName) ? "AND (p.slug ILIKE @searchPattern OR b.manifest_info->>'Name' ILIKE @searchPattern)" : "")}
                     ORDER BY manifest_info->>'Name'
                     """;
        var rows =
            await conn.QueryAsync<(string plugin_slug, int[] ver, string settings, long id, string manifest_info, string build_info)>(
                query,
                new
                {
                    btcpayVersion = btcpayVersion?.VersionParts,
                    includePreRelease = includePreRelease.Value,
                    searchPattern = $"%{searchPluginName}%"
                });

        rows.TryGetNonEnumeratedCount(out var count);
        List<PublishedVersion> versions = new(count);
        foreach (var r in rows)
        {
            PublishedVersion v = new()
            {
                ProjectSlug = r.plugin_slug,
                Version = string.Join('.', r.ver),
                BuildId = r.id,
                BuildInfo = JObject.Parse(r.build_info),
                ManifestInfo = JObject.Parse(r.manifest_info),
                Documentation = JsonConvert.DeserializeObject<PluginSettings>(r.settings)!.Documentation
            };
            versions.Add(v);
        }

        return Ok(versions);
    }

    [AllowAnonymous]
    [HttpGet("plugins/{pluginSlug}/versions/{version}")]
    public async Task<IActionResult> GetPlugin(
        [ModelBinder(typeof(PluginSlugModelBinder))]
        PluginSlug pluginSlug,
        [ModelBinder(typeof(PluginVersionModelBinder))]
        PluginVersion version)
    {
        await using var conn = await connectionFactory.Open();
        var query = """
                    SELECT v.plugin_slug, v.ver, p.settings, v.build_id, b.manifest_info, b.build_info
                    FROM versions v
                    JOIN builds b ON b.plugin_slug = v.plugin_slug AND b.id = v.build_id
                    JOIN plugins p ON b.plugin_slug = p.slug
                    WHERE v.plugin_slug = @pluginSlug AND v.ver = @version
                    AND b.manifest_info IS NOT NULL AND b.build_info IS NOT NULL 
                    LIMIT 1
                    """;
        var r = await conn.QueryFirstOrDefaultAsync(
            query,
            new { pluginSlug = pluginSlug.ToString(), version = version.VersionParts });
        if (r is null)
            return NotFound();
        return Ok(new PublishedVersion
        {
            ProjectSlug = pluginSlug.ToString(),
            Version = version.Version,
            BuildId = (long)r.build_id,
            BuildInfo = JObject.Parse(r.build_info),
            ManifestInfo = JObject.Parse(r.manifest_info),
            Documentation = JsonConvert.DeserializeObject<PluginSettings>(r.settings)!.Documentation
        });
    }

    [AllowAnonymous]
    [HttpGet("plugins/{pluginSlug}/versions/{version}/download")]
    public async Task<IActionResult> Download(
        [ModelBinder(typeof(PluginSlugModelBinder))]
        PluginSlug pluginSlug,
        [ModelBinder(typeof(PluginVersionModelBinder))]
        PluginVersion version)
    {
        await using var conn = await connectionFactory.Open();
        var url = await conn.ExecuteScalarAsync<string?>(
            "SELECT b.build_info->>'url' FROM versions v " +
            "JOIN builds b ON b.plugin_slug = v.plugin_slug AND b.id = v.build_id " +
            "WHERE v.plugin_slug=@plugin_slug AND v.ver=@version",
            new { plugin_slug = pluginSlug.ToString(), version = version.VersionParts });
        if (url is null)
            return NotFound();

        await conn.InsertEvent("Download", new JObject { ["pluginSlug"] = pluginSlug.ToString(), ["version"] = version.ToString() });
        return Redirect(url);
    }

    [HttpPost("plugins/{pluginSlug}/builds")]
    public async Task<IActionResult> CreateBuild(
        [ModelBinder(typeof(PluginSlugModelBinder))]
        PluginSlug pluginSlug,
        CreateBuildRequest model)
    {
        await using var conn = await connectionFactory.Open();
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

        _ = buildService.Build(new FullBuildId(pluginSlug, buildId));

        return Ok(new JObject
        {
            ["pluginSlug"] = pluginSlug.ToString(),
            ["buildId"] = buildId,
            ["buildUrl"] = buildUrl
        });
    }

    [HttpGet("plugins/{pluginSlug}/builds/{buildId}")]
    public async Task<IActionResult> Build(
        [ModelBinder(typeof(PluginSlugModelBinder))]
        PluginSlug pluginSlug,
        long buildId)
    {
        await using var conn = await connectionFactory.Open();
        var row =
            await conn.QueryFirstOrDefaultAsync<(string manifest_info, string build_info, DateTimeOffset created_at, bool published, bool pre_release)>(
                "SELECT manifest_info, build_info, created_at, v.ver IS NOT NULL, v.pre_release FROM builds b " +
                "LEFT JOIN versions v ON b.plugin_slug=v.plugin_slug AND b.id=v.build_id " +
                "WHERE b.plugin_slug=@pluginSlug AND id=@buildId " +
                "LIMIT 1",
                new { pluginSlug = pluginSlug.ToString(), buildId });

        var buildInfo = BuildInfo.Parse(row.build_info);
        var manifest = PluginManifest.Parse(row.manifest_info);
        BuildData vm = new()
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
        List<ValidationError> errors = (from error in modelState
            from errorMessage in error.Value.Errors
            select new ValidationError(error.Key, errorMessage.ErrorMessage)).ToList();

        return UnprocessableEntity(new { errors });
    }
}
