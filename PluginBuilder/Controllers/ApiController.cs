using System.Reflection;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.RateLimiting;
using Newtonsoft.Json.Linq;
using PluginBuilder.APIModels;
using PluginBuilder.Authentication;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.JsonConverters;
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
    BuildService buildService,
    VersionLifecycleService versionLifecycleService,
    UserManager<IdentityUser> userManager,
    UserVerifiedLogic userVerifiedLogic,
    IHttpClientFactory httpClientFactory,
    ServerEnvironment serverEnvironment)
    : ControllerBase
{
    private sealed class BuildRow
    {
        public string State { get; init; } = string.Empty;
        public string? ManifestInfo { get; init; }
        public string? BuildInfo { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public bool Published { get; init; }
        public bool? PreRelease { get; init; }
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
    [OutputCache(PolicyName = "PluginsList")]
    [EnableRateLimiting(Policies.PublicApiRateLimit)]
    public async Task<IActionResult> Plugins(
        [ModelBinder(typeof(BtcPayHostVersionModelBinder))]
        PluginVersion? btcpayVersion = null,
        bool? includePreRelease = null,
        bool? includeAllVersions = null,
        string? searchPluginName = null)
    {
        searchPluginName = searchPluginName.StripControlCharacters();

        includePreRelease ??= false;
        includeAllVersions ??= false;
        var getVersions = includeAllVersions switch
        {
            true => "get_all_versions",
            false => "get_latest_versions"
        };
        await using var conn = await connectionFactory.Open();

        var filters = new List<string>
        {
            "b.manifest_info IS NOT NULL",
            "b.build_info IS NOT NULL"
        };

        var isBtcpayV21OrHigher = btcpayVersion?.IsAtLeast(2, 1) == true;
        var hasPluginName = !string.IsNullOrWhiteSpace(searchPluginName);

        if (isBtcpayV21OrHigher)
        {
            if (hasPluginName)
            {
                filters.Add("(p.visibility = 'listed' OR p.visibility = 'unlisted')");
                filters.Add("(p.slug ILIKE @searchPattern OR b.manifest_info->>'Name' ILIKE @searchPattern)");
            }
            else
            {
                filters.Add("p.visibility = 'listed'");
            }
        }
        else
        {
            filters.Add("(p.visibility = 'listed' OR p.visibility = 'unlisted')");
            if (hasPluginName)
                filters.Add("(p.slug ILIKE @searchPattern OR b.manifest_info->>'Name' ILIKE @searchPattern)");
        }

        var whereClause = "WHERE " + string.Join(" AND ", filters);

        // This query definitely doesn't have right indexes
        var query = $"""
                     SELECT lv.plugin_slug, lv.ver, p.settings, b.id, b.manifest_info, b.build_info,
                            v.btcpay_min_ver,
                            v.btcpay_max_ver,
                            v.signatureproof->>'fingerprint' AS fingerprint
                     FROM {getVersions}(@btcpayVersion, @includePreRelease) lv
                     JOIN versions v ON v.plugin_slug = lv.plugin_slug AND v.ver = lv.ver
                     JOIN builds b ON b.plugin_slug = lv.plugin_slug AND b.id = lv.build_id
                     JOIN plugins p ON b.plugin_slug = p.slug
                     {whereClause}
                     ORDER BY manifest_info->>'Name'
                     """;
        var rows =
            await conn.QueryAsync<(string plugin_slug, int[] ver, string settings, long id, string manifest_info, string build_info, int[] btcpay_min_ver,
                int[]? btcpay_max_ver, string fingerprint)>(
                query,
                new
                {
                    btcpayVersion = btcpayVersion?.VersionParts,
                    includePreRelease = includePreRelease.Value,
                    searchPattern = $"%{searchPluginName}%"
                });

        rows.TryGetNonEnumeratedCount(out var count);
        List<PublishedVersion> versions = new(count);

        versions.AddRange(rows.Select(r =>
        {
            var manifestInfo = JObject.Parse(r.manifest_info);
            var settings = SafeJson.Deserialize<PluginSettings>((string?)r.settings);
            return CreatePublishedVersion(r.plugin_slug, r.ver, r.btcpay_min_ver, r.btcpay_max_ver, r.id, settings, manifestInfo,
                JObject.Parse(r.build_info), r.fingerprint);
        }));

        return Ok(versions);
    }

    [AllowAnonymous]
    [HttpGet("plugins/{identifier}")]
    [EnableRateLimiting(Policies.PublicApiRateLimit)]
    public async Task<IActionResult> GetPluginVersionsForDownload(
        string identifier,
        [ModelBinder(typeof(BtcPayHostVersionModelBinder))]
        PluginVersion? btcpayVersion = null,
        bool? includePreRelease = null,
        bool? includeAllVersions = null)
    {
        includePreRelease ??= false;
        includeAllVersions ??= false;
        var getVersions = includeAllVersions switch
        {
            true => "get_all_versions",
            false => "get_latest_versions"
        };
        await using var conn = await connectionFactory.Open();

        var query = $"""
                     SELECT lv.plugin_slug, lv.ver, p.settings, b.id, b.manifest_info, b.build_info,
                            v.btcpay_min_ver,
                            v.btcpay_max_ver,
                            v.signatureproof->>'fingerprint' AS fingerprint
                     FROM {getVersions}(@btcpayVersion, @includePreRelease) lv
                     JOIN versions v ON v.plugin_slug = lv.plugin_slug AND v.ver = lv.ver
                     JOIN builds b ON b.plugin_slug = lv.plugin_slug AND b.id = lv.build_id
                     JOIN plugins p ON b.plugin_slug = p.slug
                     WHERE p.identifier = @identifier
                       AND b.build_info IS NOT NULL
                       AND b.manifest_info IS NOT NULL
                     ORDER BY lv.ver DESC
                     """;

        var rows =
            await conn.QueryAsync<(string plugin_slug, int[] ver, string settings, long id, string manifest_info, string build_info, int[] btcpay_min_ver,
                int[]? btcpay_max_ver, string fingerprint)>(
                query,
                new
                {
                    btcpayVersion = btcpayVersion?.VersionParts,
                    includePreRelease = includePreRelease.Value,
                    identifier
                });

        rows.TryGetNonEnumeratedCount(out var count);
        List<PublishedVersion> versions = new(count);
        versions.AddRange(rows.Select(r =>
        {
            var manifestInfo = JObject.Parse(r.manifest_info);
            var settings = SafeJson.Deserialize<PluginSettings>((string?)r.settings);
            return CreatePublishedVersion(r.plugin_slug, r.ver, r.btcpay_min_ver, r.btcpay_max_ver, r.id, settings, manifestInfo,
                JObject.Parse(r.build_info), r.fingerprint);
        }));

        return Ok(versions);
    }

    [AllowAnonymous]
    [HttpGet("plugins/{pluginSlug}/versions/{version}")]
    [EnableRateLimiting(Policies.PublicApiRateLimit)]
    public async Task<IActionResult> GetPlugin(
        [ModelBinder(typeof(PluginSlugModelBinder))]
        PluginSlug pluginSlug,
        [ModelBinder(typeof(PluginVersionModelBinder))]
        PluginVersion version)
    {
        await using var conn = await connectionFactory.Open();
        var query = """
                    SELECT v.plugin_slug, v.ver, p.settings, v.build_id, b.manifest_info, b.build_info,
                           v.btcpay_min_ver,
                           v.btcpay_max_ver,
                           v.signatureproof->>'fingerprint' AS fingerprint
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

        var manifestInfo = JObject.Parse((string)r.manifest_info);
        var settings = SafeJson.Deserialize<PluginSettings>((string?)r.settings);
        return Ok(CreatePublishedVersion(pluginSlug.ToString(), (int[])r.ver, (int[])r.btcpay_min_ver, (int[]?)r.btcpay_max_ver,
            (long)r.build_id, settings, manifestInfo, JObject.Parse((string)r.build_info), (string?)r.fingerprint));
    }

    [AllowAnonymous]
    [HttpGet("plugins/{pluginSlug}/versions/{version}/download")]
    [EnableRateLimiting(Policies.PublicApiRateLimit)]
    public async Task<IActionResult> Download(
        [ModelBinder(typeof(PluginSlugModelBinder))]
        PluginSlug pluginSlug,
        [ModelBinder(typeof(PluginVersionModelBinder))]
        PluginVersion version)
    {
        var url = await GetArtifactUrl(pluginSlug, version);
        if (url is null)
            return NotFound();

        await using var conn = await connectionFactory.Open();
        await conn.InsertEvent("Download", new JObject { ["pluginSlug"] = pluginSlug.ToString(), ["version"] = version.ToString() });
        if (serverEnvironment.EnableLocalArtifactDownloadProxy && Uri.TryCreate(url, UriKind.Absolute, out var artifactUri) && artifactUri.IsLoopback)
        {
            return RedirectToAction(
                nameof(DownloadLoopbackArtifact),
                new { pluginSlug = pluginSlug.ToString(), version = version.ToString() });
        }

        return Redirect(url);
    }

    [AllowAnonymous]
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpGet("plugins/{pluginSlug}/versions/{version}/download-loopback")]
    [EnableRateLimiting(Policies.PublicApiRateLimit)]
    public async Task<IActionResult> DownloadLoopbackArtifact(
        [ModelBinder(typeof(PluginSlugModelBinder))]
        PluginSlug pluginSlug,
        [ModelBinder(typeof(PluginVersionModelBinder))]
        PluginVersion version)
    {
        if (!serverEnvironment.EnableLocalArtifactDownloadProxy)
            return NotFound();

        var url = await GetArtifactUrl(pluginSlug, version);
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var artifactUri) || !artifactUri.IsLoopback)
            return NotFound();

        using var response = await httpClientFactory.CreateClient().GetAsync(artifactUri, HttpContext.RequestAborted);
        if (!response.IsSuccessStatusCode)
            return StatusCode((int)response.StatusCode);

        var package = await response.Content.ReadAsByteArrayAsync(HttpContext.RequestAborted);
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/zip";
        var fileName = Path.GetFileName(artifactUri.LocalPath);
        return File(package, contentType, fileName);
    }

    private async Task<string?> GetArtifactUrl(PluginSlug pluginSlug, PluginVersion version)
    {
        await using var conn = await connectionFactory.Open();
        return await conn.ExecuteScalarAsync<string?>(
            "SELECT b.build_info->>'url' FROM versions v " +
            "JOIN builds b ON b.plugin_slug = v.plugin_slug AND b.id = v.build_id " +
            "WHERE v.plugin_slug=@plugin_slug AND v.ver=@version",
            new { plugin_slug = pluginSlug.ToString(), version = version.VersionParts });
    }

    [HttpPost("plugins/{pluginSlug}/builds")]
    public async Task<IActionResult> CreateBuild(
        [ModelBinder(typeof(PluginSlugModelBinder))]
        PluginSlug pluginSlug,
        CreateBuildRequest model)
    {
        await using var conn = await connectionFactory.Open();

        if (!await userVerifiedLogic.IsUserEmailVerifiedForPublish(User) || !await userVerifiedLogic.IsUserGithubVerified(User, conn))
            return Forbid();

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

        return CreatedAtAction(nameof(Build), new { pluginSlug = pluginSlug.ToString(), buildId }, new JObject
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
            await conn.QueryFirstOrDefaultAsync<BuildRow>(
                "SELECT state AS State, manifest_info AS ManifestInfo, build_info AS BuildInfo, created_at AS CreatedAt, v.ver IS NOT NULL AS Published, v.pre_release AS PreRelease FROM builds b " +
                "LEFT JOIN versions v ON b.plugin_slug=v.plugin_slug AND b.id=v.build_id " +
                "WHERE b.plugin_slug=@pluginSlug AND id=@buildId " +
                "LIMIT 1",
                new { pluginSlug = pluginSlug.ToString(), buildId });

        if (row is null)
            return NotFound();

        var buildInfo = row.BuildInfo is null ? null : BuildInfo.Parse(row.BuildInfo);
        var manifest = row.ManifestInfo is null ? null : PluginManifest.Parse(row.ManifestInfo);
        BuildData vm = new()
        {
            BuildId = buildId,
            ProjectSlug = pluginSlug.ToString(),
            State = row.State,
            ManifestInfo = manifest,
            BuildInfo = buildInfo,
            CreatedDate = row.CreatedAt,
            DownloadLink = buildInfo?.Url,
            Published = row.Published,
            Prerelease = row.PreRelease ?? false,
            Commit = buildInfo?.GitCommit is { Length: >= 8 } gc ? gc[..8] : buildInfo?.GitCommit,
            Repository = buildInfo?.GitRepository,
            GitRef = buildInfo?.GitRef
        };

        return Ok(vm);
    }

    [HttpGet("plugins/{pluginSlug}/builds")]
    public async Task<IActionResult> ListBuilds(
        [ModelBinder(typeof(PluginSlugModelBinder))]
        PluginSlug pluginSlug)
    {
        await using var conn = await connectionFactory.Open();
        var rows = await conn
            .QueryAsync<(long id, string state, string? manifest_info, string? build_info,
                DateTimeOffset created_at, bool published, bool pre_release)>(
                "SELECT id, state, manifest_info, build_info, created_at, " +
                "v.ver IS NOT NULL, v.pre_release " +
                "FROM builds b " +
                "LEFT JOIN versions v ON b.plugin_slug=v.plugin_slug AND b.id=v.build_id " +
                "WHERE b.plugin_slug = @pluginSlug " +
                "ORDER BY id DESC " +
                "LIMIT 50",
                new { pluginSlug = pluginSlug.ToString() });

        var builds = rows.Select(row =>
        {
            var buildInfo = row.build_info is null ? null : BuildInfo.Parse(row.build_info);
            var manifest = row.manifest_info is null ? null : PluginManifest.Parse(row.manifest_info);
            return new BuildData
            {
                BuildId = row.id,
                ProjectSlug = pluginSlug.ToString(),
                State = row.state,
                ManifestInfo = manifest,
                BuildInfo = buildInfo,
                CreatedDate = row.created_at,
                DownloadLink = buildInfo?.Url,
                Published = row.published,
                Prerelease = row.pre_release,
                Commit = buildInfo?.GitCommit is { Length: >= 8 } gc ? gc[..8] : buildInfo?.GitCommit,
                Repository = buildInfo?.GitRepository,
                GitRef = buildInfo?.GitRef
            };
        }).ToList();

        return Ok(builds);
    }

    [HttpPost("plugins/{pluginSlug}/versions/{version}/release")]
    public async Task<IActionResult> ReleaseVersion(
        [ModelBinder(typeof(PluginSlugModelBinder))]
        PluginSlug pluginSlug,
        [ModelBinder(typeof(PluginVersionModelBinder))]
        PluginVersion version,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)]
        ReleaseVersionRequest? request = null)
    {
        byte[]? signatureBytes = null;
        if (!string.IsNullOrEmpty(request?.Signature))
        {
            try
            {
                signatureBytes = Convert.FromBase64String(request.Signature);
            }
            catch (FormatException)
            {
                ModelState.AddModelError(nameof(request.Signature), "Signature must be valid base64");
                return ValidationErrorResult(ModelState);
            }
        }

        var result = await versionLifecycleService.ReleaseAsync(pluginSlug, version, userManager.GetUserId(User)!, signatureBytes);
        if (!result.Success)
            return VersionLifecycleFailureResult(result, nameof(ReleaseVersionRequest.Signature));

        return Ok(new { version = version.ToString(), released = true });
    }

    [HttpPost("plugins/{pluginSlug}/versions/{version}/unrelease")]
    public async Task<IActionResult> UnreleaseVersion(
        [ModelBinder(typeof(PluginSlugModelBinder))]
        PluginSlug pluginSlug,
        [ModelBinder(typeof(PluginVersionModelBinder))]
        PluginVersion version)
    {
        var result = await versionLifecycleService.UnreleaseAsync(pluginSlug, version);
        if (!result.Success)
            return VersionLifecycleFailureResult(result);

        return Ok(new { version = version.ToString(), released = false });
    }

    [HttpDelete("plugins/{pluginSlug}/versions/{version}")]
    public async Task<IActionResult> RemoveVersion(
        [ModelBinder(typeof(PluginSlugModelBinder))]
        PluginSlug pluginSlug,
        [ModelBinder(typeof(PluginVersionModelBinder))]
        PluginVersion version)
    {
        var result = await versionLifecycleService.RemoveAsync(pluginSlug, version);
        if (!result.Success)
            return VersionLifecycleFailureResult(result);

        return Ok(new { version = version.ToString(), removed = true });
    }

    [AllowAnonymous]
    [HttpPost("plugins/updates")]
    [EnableRateLimiting(Policies.PublicApiRateLimit)]
    public async Task<IActionResult> GetInstalledPluginsUpdates(
        [FromBody] InstalledPluginRequest[] plugins,
        [ModelBinder(typeof(BtcPayHostVersionModelBinder))]
        PluginVersion? btcpayVersion = null,
        bool? includePreRelease = null)
    {
        includePreRelease ??= false;

        if (plugins.Length == 0)
            return BadRequest(new { errors = new[] { new ValidationError(nameof(plugins), "At least one plugin must be provided.") } });

        var identifiers = plugins
            .Select(p => p.Identifier)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (identifiers.Length == 0)
            return Ok(Array.Empty<PublishedVersion>());

        await using var conn = await connectionFactory.Open();

        var query = """
                    SELECT lv.plugin_slug, lv.ver, p.identifier, p.settings, b.id, b.manifest_info, b.build_info,
                           v.btcpay_min_ver,
                           v.btcpay_max_ver,
                           v.signatureproof->>'fingerprint' AS fingerprint
                    FROM get_latest_versions(@btcpayVersion, @includePreRelease) lv
                    JOIN versions v ON v.plugin_slug = lv.plugin_slug AND v.ver = lv.ver
                    JOIN builds b ON b.plugin_slug = lv.plugin_slug AND b.id = lv.build_id
                    JOIN plugins p ON b.plugin_slug = p.slug
                    WHERE p.identifier = ANY(@identifiers)
                      AND b.build_info IS NOT NULL
                      AND b.manifest_info IS NOT NULL
                      AND p.visibility <> 'hidden'
                    """;

        var rows = await conn
            .QueryAsync<(string plugin_slug, int[] ver, string identifier, string settings, long id, string manifest_info, string build_info,
                int[] btcpay_min_ver, int[]? btcpay_max_ver, string fingerprint
                )>(
                query,
                new
                {
                    btcpayVersion = btcpayVersion?.VersionParts,
                    includePreRelease = includePreRelease.Value,
                    identifiers
                });

        var updates =
        (
            from row in rows
            let manifestInfo = JObject.Parse(row.manifest_info)
            let settings = SafeJson.Deserialize<PluginSettings>((string?)row.settings)
            select CreatePublishedVersion(row.plugin_slug, row.ver, row.btcpay_min_ver, row.btcpay_max_ver, row.id, settings, manifestInfo,
                JObject.Parse(row.build_info), row.fingerprint)
        ).ToList();

        return Ok(updates);
    }

    private IActionResult ValidationErrorResult(ModelStateDictionary modelState)
    {
        List<ValidationError> errors = (from error in modelState
            from errorMessage in error.Value.Errors
            select new ValidationError(error.Key, errorMessage.ErrorMessage)).ToList();

        return UnprocessableEntity(new { errors });
    }

    private IActionResult VersionLifecycleFailureResult(VersionLifecycleResult result, string? signaturePath = null)
    {
        if (result.FailureCode == VersionLifecycleFailureCode.NotFound)
            return NotFound();

        var errorPath = result.FailureCode == VersionLifecycleFailureCode.SignatureVerificationFailed
            ? signaturePath ?? string.Empty
            : string.Empty;
        ModelState.AddModelError(errorPath, result.Message ?? "Version lifecycle operation failed");
        return ValidationErrorResult(ModelState);
    }

    private string PluginPublicPage(string slug)
    {
        return Url.Action(nameof(HomeController.GetPluginDetails), "Home", new { pluginSlug = slug }, Request.Scheme, Request.Host.ToString());
    }

    private PublishedVersion CreatePublishedVersion(
        string pluginSlug,
        int[] version,
        int[] btcpayMinVersion,
        int[]? btcpayMaxVersion,
        long buildId,
        PluginSettings? settings,
        JObject manifestInfo,
        JObject buildInfo,
        string? fingerprint)
    {
        var effectiveManifestInfo = ApplyEffectiveBtcPayCompatibility(manifestInfo, btcpayMinVersion, btcpayMaxVersion);

        return new PublishedVersion
        {
            PluginTitle = settings?.PluginTitle ?? manifestInfo["Name"]?.ToString(),
            Description = settings?.Description ?? manifestInfo["Description"]?.ToString(),
            ProjectSlug = pluginSlug,
            Version = string.Join('.', version),
            BTCPayMinVersion = string.Join('.', btcpayMinVersion),
            BTCPayMaxVersion = btcpayMaxVersion is { Length: > 0 } ? string.Join('.', btcpayMaxVersion) : null,
            BuildId = buildId,
            BuildInfo = buildInfo,
            ManifestInfo = effectiveManifestInfo,
            PluginLogo = settings?.Logo,
            Documentation = PluginPublicPage(pluginSlug),
            VideoUrl = settings?.VideoUrl,
            Fingerprint = fingerprint
        };
    }

    private static JObject ApplyEffectiveBtcPayCompatibility(JObject manifestInfo, int[] btcpayMinVersion, int[]? btcpayMaxVersion)
    {
        var clone = (JObject)manifestInfo.DeepClone();
        var dependencies = clone["Dependencies"] as JArray;
        var btcpayDependency = dependencies?
            .OfType<JObject>()
            .FirstOrDefault(d => string.Equals(d["Identifier"]?.ToString(), "BTCPayServer", StringComparison.Ordinal));

        var isUnrestricted = btcpayMaxVersion is null && btcpayMinVersion.All(part => part == 0);
        if (isUnrestricted)
        {
            btcpayDependency?.Remove();
            return clone;
        }

        var effectiveCondition = $">={string.Join('.', btcpayMinVersion)}";
        if (btcpayMaxVersion is { Length: > 0 })
            effectiveCondition += $" && <={string.Join('.', btcpayMaxVersion)}";

        if (btcpayDependency is not null)
        {
            btcpayDependency["Condition"] = effectiveCondition;
            return clone;
        }

        dependencies ??= [];
        clone["Dependencies"] = dependencies;
        dependencies.Add(new JObject
        {
            ["Identifier"] = "BTCPayServer",
            ["Condition"] = effectiveCondition
        });

        return clone;
    }
}
