using Microsoft.AspNetCore.Authorization;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using PluginBuilder.ModelBinders;
using PluginBuilder.Services;
using PluginBuilder.ViewModels;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;

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

        [HttpGet]
        [Route("create")]
        public async Task<IActionResult> CreateBuild(
            [ModelBinder(typeof(PluginSlugModelBinder))]
            PluginSlug pluginSlug, long? copyBuild = null)
        {
            var model = new CreateBuildViewModel();
            if (copyBuild is long buildId)
            {
                using var conn = await ConnectionFactory.Open();
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
        [HttpPost]
        [Route("create")]
        public async Task<IActionResult> CreateBuild(
            [ModelBinder(typeof(PluginSlugModelBinder))]
            PluginSlug pluginSlug,
            CreateBuildViewModel model)
        {
            using var conn = await ConnectionFactory.Open();
            var buildId = await conn.NewBuild(pluginSlug, model.ToBuildParameter());
            _ = BuildService.Build(new FullBuildId(pluginSlug, buildId));
            //return RedirectToAction(nameof(Build));
            return RedirectToAction(nameof(Dashboard), new { pluginSlug = pluginSlug.ToString() });
        }

        [HttpGet]
        [Route("builds/{buildId}")]
        public async Task<IActionResult> Build(
           [ModelBinder(typeof(PluginSlugModelBinder))]
            PluginSlug pluginSlug,
           long buildId)
        {
            using var conn = await ConnectionFactory.Open();
            var row = await conn.QueryFirstOrDefaultAsync<(string manifest_info, string build_info, DateTimeOffset created_at, bool published)>(
                "SELECT manifest_info, build_info, created_at, v.ver IS NOT NULL FROM builds b " +
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
            vm.Version = manifest?.VersionString;
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
            var rows = await conn.QueryAsync<(long id, string state, string? manifest_info, string? build_info, DateTimeOffset created_at, bool published)>
                ("SELECT id, state, manifest_info, build_info, created_at, v.ver IS NOT NULL " +
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
                b.Version = manifest?.VersionString;
                b.Date = (DateTimeOffset.UtcNow - row.created_at).ToTimeAgo();
                b.RepositoryLink = GetUrl(buildInfo);
                b.DownloadLink = buildInfo?.Url;
                b.Error = buildInfo?.Error;
                b.Published = row.published;
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
