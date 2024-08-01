using Microsoft.AspNetCore.Authorization;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using PluginBuilder.ModelBinders;
using PluginBuilder.Services;
using PluginBuilder.ViewModels;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using PluginBuilder.Components.PluginVersion;
using PluginBuilder.Extension;

namespace PluginBuilder.Controllers
{
    [Authorize(Policy = Policies.OwnPlugin)]
    [Route("/plugins/{pluginSlug}")]
    public class PluginController : Controller
    {
        public PluginController(
            DBConnectionFactory connectionFactory,
            BuildService buildService,
            AzureStorageClient azureStorageClient,
            EventAggregator eventAggregator)
        {
            AzureStorageClient = azureStorageClient;
            ConnectionFactory = connectionFactory;
            BuildService = buildService;
            EventAggregator = eventAggregator;
        }

        private DBConnectionFactory ConnectionFactory { get; }
        private BuildService BuildService { get; }
        private EventAggregator EventAggregator { get; }
        public AzureStorageClient AzureStorageClient { get; }

        [HttpGet("settings")]
        public async Task<IActionResult> Settings(
            [ModelBinder(typeof(PluginSlugModelBinder))]
            PluginSlug pluginSlug)
        {
            await using var conn = await ConnectionFactory.Open();
            var settings = await conn.GetSettings(pluginSlug);
            
            
            if (settings is null)
                return NotFound();

            var viewModel = settings.ToPluginSettingViewModel();
            return View(viewModel);
        }
        
        [HttpPost("settings")]
        public async Task<IActionResult> Settings(
          [ModelBinder(typeof(PluginSlugModelBinder))]
            PluginSlug pluginSlug,
            PluginSettingViewModel settingViewModel)
        {
            if (settingViewModel is null)
                return NotFound();
            string url = settingViewModel.Logo;
            if (!string.IsNullOrEmpty(settingViewModel.Documentation) && !Uri.TryCreate(settingViewModel.Documentation, UriKind.Absolute, out _))
            {
                ModelState.AddModelError(nameof(settingViewModel.Documentation), "Documentation should be an absolute URL");
                return View(settingViewModel);
            }
            if (!string.IsNullOrEmpty(settingViewModel.GitRepository) && !Uri.TryCreate(settingViewModel.GitRepository, UriKind.Absolute, out _))
            {
                ModelState.AddModelError(nameof(settingViewModel.GitRepository), "Git repository should be an absolute URL");
                return View(settingViewModel);
            }
            if (settingViewModel.PluginLogo != null)
            {
                try
                {
                    if (!settingViewModel.PluginLogo.Length.IsImageValidSize())
                    {
                        ModelState.AddModelError(nameof(settingViewModel.PluginLogo), "The file size exceeds the 1 MB limit");
                        return View(settingViewModel);
                    }
                    if (!settingViewModel.PluginLogo.FileName.IsFileValidImage())
                    {
                        ModelState.AddModelError(nameof(settingViewModel.PluginLogo), "Invalid file type. Only images are allowed");
                        return View(settingViewModel);
                    }
                    if (!settingViewModel.PluginLogo.FileName.IsValidFileName())
                    {
                        ModelState.AddModelError(nameof(settingViewModel.PluginLogo), "Could not complete plugin creation. File has invalid name");
                        return View(settingViewModel);
                    }
                    url = await AzureStorageClient.UploadFileAsync(settingViewModel.PluginLogo, $"{settingViewModel.PluginLogo.FileName}");
                    settingViewModel.Logo = url;
                }
                catch (Exception)
                {
                    ModelState.AddModelError(nameof(settingViewModel.Logo), "Could not complete settings upload. An error occurred while uploading logo");
                    return View(settingViewModel);
                }
            }

            var settings = settingViewModel.ToPluginSettings();
            await using var conn = await ConnectionFactory.Open();
            await conn.SetSettings(pluginSlug, settings);
            TempData[TempDataConstant.SuccessMessage] = "Settings updated";
            return RedirectToAction(nameof(Settings),new { pluginSlug });
        }

        [HttpGet("create")]
        public async Task<IActionResult> CreateBuild(
            [ModelBinder(typeof(PluginSlugModelBinder))]
            PluginSlug pluginSlug, long? copyBuild = null)
        {
            await using var conn = await ConnectionFactory.Open();
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
            await using var conn = await ConnectionFactory.Open();
            var buildId = await conn.NewBuild(pluginSlug, model.ToBuildParameter());
            _ = BuildService.Build(new FullBuildId(pluginSlug, buildId));
            return RedirectToAction(nameof(Build), new { pluginSlug = pluginSlug.ToString(), buildId });
        }

        [HttpPost("versions/{version}/release")]
        public async Task<IActionResult> Release(
            [ModelBinder(typeof(PluginSlugModelBinder))]
            PluginSlug pluginSlug,
            [ModelBinder(typeof(PluginVersionModelBinder))]
            PluginVersion version, string command )
        {
            await using var conn = await ConnectionFactory.Open();

            if (command == "remove")
            {
                var pluginBuild = await conn.QueryFirstOrDefaultAsync<(long buildId, string identifier)>("SELECT v.build_id, p.identifier FROM versions v JOIN plugins p ON v.plugin_slug = p.slug WHERE plugin_slug=@pluginSlug AND ver=@version",
                    new
                    {
                        pluginSlug = pluginSlug.ToString(),
                        version = version.VersionParts
                    });
                var fullBuildId = new FullBuildId(pluginSlug, pluginBuild.buildId);
                await conn.ExecuteAsync("DELETE FROM versions WHERE plugin_slug=@pluginSlug AND ver=@version",
                    new { pluginSlug = pluginSlug.ToString(), version = version.VersionParts });
                await BuildService.UpdateBuild(fullBuildId, BuildStates.Removed, null, null);
                return RedirectToAction(nameof(Build), new
                {
                    pluginSlug = pluginSlug.ToString(),
                    pluginBuild.buildId
                });
            }
            else
            {
                await conn.ExecuteAsync("UPDATE versions SET pre_release=@preRelease WHERE plugin_slug=@pluginSlug AND ver=@version",
                    new
                    {
                        pluginSlug = pluginSlug.ToString(),
                        version = version.VersionParts,
                        preRelease = command == "unrelease"
                    });
                TempData[TempDataConstant.SuccessMessage] =
                    $"Version {version} {(command == "release" ? "released" : "unreleased")}";
                return RedirectToAction(nameof(Version), new
                {
                    pluginSlug = pluginSlug.ToString(),
                    version = version.ToString()
                });
            }
        }

        [HttpGet("versions/{version}")]
        public async Task<IActionResult> Version(
            [ModelBinder(typeof(PluginSlugModelBinder))]
            PluginSlug pluginSlug,
            [ModelBinder(typeof(PluginVersionModelBinder))]
            PluginVersion version)
        {
            await using var conn = await ConnectionFactory.Open();
            var buildId = conn.ExecuteScalar<long>("SELECT build_id FROM versions WHERE plugin_slug=@pluginSlug AND ver=@version",
            new
            {
                pluginSlug = pluginSlug.ToString(),
                version = version.VersionParts
            });
            return RedirectToAction(nameof(Build), new
            { 
                pluginSlug = pluginSlug.ToString(),
                buildId
            });
        }

        [HttpGet("builds/{buildId}")]
        public async Task<IActionResult> Build(
           [ModelBinder(typeof(PluginSlugModelBinder))]
            PluginSlug pluginSlug,
           long buildId)
        {
            await using var conn = await ConnectionFactory.Open();
            var row = await conn.QueryFirstOrDefaultAsync<(string manifest_info, string build_info, string state, DateTimeOffset created_at, bool published, bool pre_release)>(
                "SELECT manifest_info, build_info, state, created_at, v.ver IS NOT NULL, v.pre_release FROM builds b " +
                "LEFT JOIN versions v ON b.plugin_slug=v.plugin_slug AND b.id=v.build_id " +
                "WHERE b.plugin_slug=@pluginSlug AND id=@buildId " +
                "LIMIT 1",
                new
                {
                    pluginSlug = pluginSlug.ToString(),
                    buildId
                });
            var logLines = await conn.QueryAsync<string>(
                "SELECT logs FROM builds_logs " +
                "WHERE plugin_slug=@pluginSlug AND build_id=@buildId " +
                "ORDER BY created_at;",
                new
                {
                    pluginSlug = pluginSlug.ToString(),
                    buildId
                });
            var logs = string.Join("\r\n", logLines);
            var vm = new BuildViewModel();
            var buildInfo = row.build_info is null ? null : BuildInfo.Parse(row.build_info);
            var manifest = row.manifest_info is null ? null : PluginManifest.Parse(row.manifest_info);
            vm.FullBuildId = new FullBuildId(pluginSlug, buildId);
            vm.ManifestInfo = NiceJson(row.manifest_info);
            vm.BuildInfo = buildInfo?.ToString(Formatting.Indented);
            vm.DownloadLink = buildInfo?.Url;
            vm.State = row.state;
            vm.CreatedDate = (DateTimeOffset.UtcNow - row.created_at).ToTimeAgo();
            vm.Commit = buildInfo?.GitCommit?.Substring(0, 8);
            vm.Repository = buildInfo?.GitRepository;
            vm.GitRef = buildInfo?.GitRef;
            vm.Version = PluginVersionViewModel.CreateOrNull(manifest?.Version?.ToString(), row.published, row.pre_release, row.state, pluginSlug.ToString());
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
            return data.ToString(Formatting.Indented);
        }

        [HttpGet("")]
        public async Task<IActionResult> Dashboard(
            [ModelBinder(typeof(PluginSlugModelBinder))]
            PluginSlug pluginSlug)
        {
            await using var conn = await ConnectionFactory.Open();
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
                b.Version = PluginVersionViewModel.CreateOrNull(manifest?.Version?.ToString(), row.published, row.pre_release, row.state, pluginSlug.ToString());
                b.Date = (DateTimeOffset.UtcNow - row.created_at).ToTimeAgo();
                b.RepositoryLink = GetUrl(buildInfo);
                b.DownloadLink = buildInfo?.Url;
                b.Error = buildInfo?.Error;
            }
            return View(vm);
        }

        public static string? GetUrl(BuildInfo? buildInfo)
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
