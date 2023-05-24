using Microsoft.AspNetCore.Authorization;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using PluginBuilder.ModelBinders;
using PluginBuilder.Services;
using PluginBuilder.ViewModels;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using PluginBuilder.Components.PluginVersion;

namespace PluginBuilder.Controllers
{
    [Authorize(Policy = Policies.OwnPlugin)]
    [Route("/plugins/{pluginSlug}")]
    public class PluginController : Controller
    {
        public PluginController(
            DBConnectionFactory connectionFactory,
            BuildService buildService)
        {
            ConnectionFactory = connectionFactory;
            BuildService = buildService;
        }

        public DBConnectionFactory ConnectionFactory { get; }
        public BuildService BuildService { get; }

        [HttpGet("settings")]
        public async Task<IActionResult> Settings(
            [ModelBinder(typeof(PluginSlugModelBinder))]
            PluginSlug pluginSlug)
        {
            using var conn = await ConnectionFactory.Open();
            var settings = await conn.GetSettings(pluginSlug);
            if (settings is null)
                return NotFound();
            return View(settings);
        }
        
        [HttpPost("settings")]
        public async Task<IActionResult> Settings(
          [ModelBinder(typeof(PluginSlugModelBinder))]
            PluginSlug pluginSlug,
            PluginSettings settings)
        {
            if (settings is null)
                return NotFound();
            if (!string.IsNullOrEmpty(settings.Documentation) && !Uri.TryCreate(settings.Documentation, UriKind.Absolute, out _))
            {
                ModelState.AddModelError(nameof(settings.Documentation), "Documentation should be an absolute URL");
            }
            if (!string.IsNullOrEmpty(settings.GitRepository) && !Uri.TryCreate(settings.GitRepository, UriKind.Absolute, out _))
            {
                ModelState.AddModelError(nameof(settings.GitRepository), "Git repository should be an absolute URL");
            }
            if (!ModelState.IsValid)
                return View(settings);
            using var conn = await ConnectionFactory.Open();
            await conn.SetSettings(pluginSlug, settings);
            TempData[TempDataConstant.SuccessMessage] = "Settings updated";
            return RedirectToAction(nameof(Settings),new { pluginSlug });
        }

        [HttpGet("create")]
        public async Task<IActionResult> CreateBuild(
            [ModelBinder(typeof(PluginSlugModelBinder))]
            PluginSlug pluginSlug, long? copyBuild = null)
        {
            using var conn = await ConnectionFactory.Open();
            var settings = await conn.GetSettings(pluginSlug);
            var model = new CreateBuildViewModel
            {
                GitRepository = settings?.GitRepository,
                GitRef = settings?.GitRef,
                PluginDirectory = settings?.PluginDirectory,
                BuildConfig = settings?.BuildConfig
            };
            
            if (copyBuild is long buildId)
            {
                var buildInfo = await conn.QueryFirstOrDefaultAsync<string>("SELECT build_info FROM builds WHERE plugin_slug=@pluginSlug AND id=@buildId",
                    new
                    {
                        buildId = buildId,
                        pluginSlug = pluginSlug.ToString()
                    });
                if (buildInfo != null)
                {
                    var bi = BuildInfo.Parse(buildInfo);
                    model.GitRepository = bi.GitRepository;
                    model.GitRef = bi.GitRef;
                    model.PluginDirectory = bi.PluginDir;
                    model.BuildConfig = bi.BuildConfig;
                }
            }
            return View(model);
        }

        [HttpPost("create")]
        public async Task<IActionResult> CreateBuild(
            [ModelBinder(typeof(PluginSlugModelBinder))]
            PluginSlug pluginSlug,
            CreateBuildViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);
            using var conn = await ConnectionFactory.Open();
            var buildId = await conn.NewBuild(pluginSlug, model.ToBuildParameter());
            _ = BuildService.Build(new FullBuildId(pluginSlug, buildId));
            return RedirectToAction(nameof(Build), new { pluginSlug = pluginSlug.ToString(), buildId });
        }

        [HttpPost("versions/{version}/release")]
        public async Task<IActionResult> Release(
            [ModelBinder(typeof(PluginSlugModelBinder))]
            PluginSlug pluginSlug,
            [ModelBinder(typeof(PluginVersionModelBinder))]
            PluginBuilder.PluginVersion version)
        {
            using var conn = await ConnectionFactory.Open();
            await conn.ExecuteAsync("UPDATE versions SET pre_release='f' WHERE plugin_slug=@pluginSlug AND ver=@version",
                new
                {
                    pluginSlug = pluginSlug.ToString(),
                    version = version.VersionParts
                });
            TempData[TempDataConstant.SuccessMessage] = "New release published";
            return RedirectToAction(nameof(Version), new
            {
                pluginSlug = pluginSlug.ToString(),
                version = version.ToString()
            });
        }

        [HttpGet("versions/{version}")]
        public async Task<IActionResult> Version(
            [ModelBinder(typeof(PluginSlugModelBinder))]
            PluginSlug pluginSlug,
            [ModelBinder(typeof(PluginVersionModelBinder))]
            PluginBuilder.PluginVersion version)
        {
            using var conn = await ConnectionFactory.Open();
            var buildId = conn.ExecuteScalar<long>("SELECT build_id FROM versions WHERE plugin_slug=@pluginSlug AND ver=@version",
            new
            {
                pluginSlug = pluginSlug.ToString(),
                version = version.VersionParts
            });
            return RedirectToAction(nameof(Build), new
            { 
                pluginSlug = pluginSlug.ToString(),
                buildId = buildId
            });
        }

        [HttpGet("builds/{buildId}")]
        public async Task<IActionResult> Build(
           [ModelBinder(typeof(PluginSlugModelBinder))]
            PluginSlug pluginSlug,
           long buildId)
        {
            using var conn = await ConnectionFactory.Open();
            var row = await conn.QueryFirstOrDefaultAsync<(string manifest_info, string build_info, DateTimeOffset created_at, bool published, bool pre_release)>(
                "SELECT manifest_info, build_info, created_at, v.ver IS NOT NULL, v.pre_release FROM builds b " +
                "LEFT JOIN versions v ON b.plugin_slug=v.plugin_slug AND b.id=v.build_id " +
                "WHERE b.plugin_slug=@pluginSlug AND id=@buildId " +
                "LIMIT 1",
                new
                {
                    pluginSlug = pluginSlug.ToString(),
                    buildId = buildId
                });
            var logLines = await conn.QueryAsync<string>(
                "SELECT logs FROM builds_logs " +
                "WHERE plugin_slug=@pluginSlug AND build_id=@buildId " +
                "ORDER BY created_at;",
                new
                {
                    pluginSlug = pluginSlug.ToString(),
                    buildId = buildId
                });
            var logs = String.Join("\r\n", logLines);
            BuildViewModel vm = new BuildViewModel();
            var buildInfo = row.build_info is null ? null : BuildInfo.Parse(row.build_info);
            var manifest = row.manifest_info is null ? null : PluginManifest.Parse(row.manifest_info);
            vm.FullBuildId = new FullBuildId(pluginSlug, buildId);
            vm.ManifestInfo = NiceJson(row.manifest_info);
            vm.BuildInfo = buildInfo?.ToString(Formatting.Indented);
            vm.DownloadLink = buildInfo?.Url;
            vm.CreatedDate = (DateTimeOffset.UtcNow - row.created_at).ToTimeAgo();
            //vm.State = row.state;
            vm.Commit = buildInfo?.GitCommit?.Substring(0, 8);
            vm.Repository = buildInfo?.GitRepository;
            vm.GitRef = buildInfo?.GitRef;
            vm.Version = PluginVersionViewModel.CreateOrNull(manifest?.Version?.ToString(), row.published, row.pre_release);
            vm.RepositoryLink = GetUrl(buildInfo);
            vm.DownloadLink = buildInfo?.Url;
            //vm.Error = buildInfo?.Error;
            vm.Published = row.published;
            //var buildId = await conn.NewBuild(pluginSlug);
            //_ = BuildService.Build(new FullBuildId(pluginSlug, buildId), model.ToBuildParameter());
            if (logs != "")
                vm.Logs = logs;
            return View(vm);
        }

        private string? NiceJson(string? json)
        {
            if (json is null)
                return null;
            var data = JObject.Parse(json);
            data = new JObject(data.Properties().OrderBy(p => p.Name));
            return data.ToString(Newtonsoft.Json.Formatting.Indented);
        }

        [HttpGet]
        public async Task<IActionResult> Dashboard(
            [ModelBinder(typeof(PluginSlugModelBinder))]
            PluginSlug pluginSlug)
        {
            HttpContext.Response.Cookies.Append(Cookies.PluginSlug, pluginSlug.ToString());
            using var conn = await ConnectionFactory.Open();
            var rows = await conn.QueryAsync<(long id, string state, string? manifest_info, string? build_info, DateTimeOffset created_at, bool published, bool pre_release)>
                ("SELECT id, state, manifest_info, build_info, created_at, v.ver IS NOT NULL, v.pre_release " +
                "FROM builds b " +
                "LEFT JOIN versions v ON b.plugin_slug=v.plugin_slug AND b.id=v.build_id " +
                "WHERE b.plugin_slug = @pluginSlug " +
                "ORDER BY id DESC " +
                "LIMIT 50", new { pluginSlug = pluginSlug.ToString() });
            var vm = new BuildListViewModel();
            foreach (var row in rows)
            {
                var b = new BuildListViewModel.BuildViewModel();
                var buildInfo = row.build_info is null ? null : BuildInfo.Parse(row.build_info);
                var manifest = row.manifest_info is null ? null : PluginManifest.Parse(row.manifest_info);
                vm.Builds.Add(b);
                b.BuildId = row.id;
                b.State = row.state;
                b.Commit = buildInfo?.GitCommit?.Substring(0, 8);
                b.Repository = buildInfo?.GitRepository;
                b.GitRef = buildInfo?.GitRef;
                b.Version = Components.PluginVersion.PluginVersionViewModel.CreateOrNull(manifest?.Version?.ToString(), row.published, row.pre_release);
                b.Date = (DateTimeOffset.UtcNow - row.created_at).ToTimeAgo();
                b.RepositoryLink = GetUrl(buildInfo);
                b.DownloadLink = buildInfo?.Url;
                b.Error = buildInfo?.Error;
            }
            return View(vm);
        }

        private static string? GetUrl(BuildInfo? buildInfo)
        {
            if (buildInfo?.GitRepository is String repo && buildInfo?.GitCommit is String commit)
            {
                string? repoName = null;
                // git@github.com:Kukks/btcpayserver.git
                if (repo.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
                {
                    repoName = repo.Substring("git@github.com:".Length);
                }
                // https://github.com/Kukks/btcpayserver.git
                // https://github.com/Kukks/btcpayserver
                else if (repo.StartsWith("https://github.com/"))
                {
                    repoName = repo.Substring("https://github.com/".Length);
                }
                if (repoName is not null)
                {
                    // Kukks/btcpayserver
                    if (repoName.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                        repoName = repoName.Substring(0, repoName.Length - 4);
                    // https://github.com/Kukks/btcpayserver/tree/plugins/collection/Plugins/BTCPayServer.Plugins.AOPP
                    string link = $"https://github.com/{repoName}/tree/{commit}";
                    if (buildInfo?.PluginDir is String pluginDir)
                        link += $"/{pluginDir}";
                    return link;
                }
            }
            return null;
        }
    }
}
