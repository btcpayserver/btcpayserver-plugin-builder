using Dapper;
using Newtonsoft.Json.Linq;
using PluginBuilder.Events;

namespace PluginBuilder.Services
{
    public class BuildServiceException : Exception
    {
        public BuildServiceException(string message) : base(message)
        {

        }
    }
    public class BuildService
    {
        public BuildService(
            ILogger<BuildService> logger,
            ProcessRunner processRunner,
            DBConnectionFactory connectionFactory,
            EventAggregator eventAggregator,
            AzureStorageClient azureStorageClient)
        {
            Logger = logger;
            ProcessRunner = processRunner;
            ConnectionFactory = connectionFactory;
            EventAggregator = eventAggregator;
            AzureStorageClient = azureStorageClient;
        }

        public ILogger<BuildService> Logger { get; }
        public ProcessRunner ProcessRunner { get; }
        public DBConnectionFactory ConnectionFactory { get; }
        public EventAggregator EventAggregator { get; }
        public AzureStorageClient AzureStorageClient { get; }

        public async Task Build(FullBuildId fullBuildId)
        {
            using var buildLogCapture = new BuildOutputCapture(fullBuildId, ConnectionFactory);
            List<string> args = new List<string>();
            var buildParameters = await GetBuildInfo(fullBuildId);
            // Create the volumes where the artifacts will be stored
            args.AddRange(new[] { "volume", "create" });
            args.AddRange(new[] { "--label", $"BTCPAY_PLUGIN_BUILD={fullBuildId}" });
            var output = new OutputCapture();
            var code = await ProcessRunner.RunAsync(new ProcessSpec
            {
                Executable = "docker",
                Arguments = args.ToArray(),
                OutputCapture = output
            }, default);
            if (code != 0)
                throw new BuildServiceException("docker volume create failed");
            var volume = output.ToString().Trim();
            args.Clear();

            // Then let's build by running our image plugin-builder (built in DockerStartupHostedService)
            var info = new JObject();

            args.Add("run");
            args.AddRange(new[] { "--env", $"GIT_REPO={buildParameters.GitRepository}" });
            info["gitRepository"] = buildParameters.GitRepository;
            info["dockerVolume"] = volume;
            if (buildParameters.GitRef != null)
            {
                args.AddRange(new[] { "--env", $"GIT_REF={buildParameters.GitRef}" });
                info["gitRef"] = buildParameters.GitRef;
            }
            if (buildParameters.PluginDir != null)
            {
                args.AddRange(new[] { "--env", $"PLUGIN_DIR={buildParameters.PluginDir}" });
                info["pluginDir"] = buildParameters.PluginDir;
            }
            if (buildParameters.BuildConfig != null)
            {
                args.AddRange(new[] { "--env", $"BUILD_CONFIG={buildParameters.BuildConfig}" });
                info["buildConfig"] = buildParameters.BuildConfig;
            }

            args.AddRange(new[] { "-v", $"{volume}:/out" });
            args.AddRange(new[] { "--rm" });
            args.Add("plugin-builder");
            await UpdateBuild(fullBuildId, "running", info);
            JObject buildEnv;
            try
            {
                code = await ProcessRunner.RunAsync(new ProcessSpec
                {
                    Executable = "docker",
                    Arguments = args.ToArray(),
                    OutputCapture = buildLogCapture,
                    ErrorCapture = buildLogCapture,
                    OnOutput = (_, eventArgs) =>
                    {
                        if (!string.IsNullOrEmpty(eventArgs.Data))
                            EventAggregator.Publish(new BuildLogUpdated(fullBuildId, eventArgs.Data));
                    },
                    OnError = (_, eventArgs) =>
                    {
                        if (!string.IsNullOrEmpty(eventArgs.Data))
                            EventAggregator.Publish(new BuildLogUpdated(fullBuildId, eventArgs.Data));
                    }
                }, default);
                if (code != 0)
                    throw new BuildServiceException("docker build failed");

                string buildEnvStr = await ReadFileInVolume(volume, "build-env.json");
                buildEnv = JObject.Parse(buildEnvStr);
            }
            catch (Exception err)
            {
                await UpdateBuild(fullBuildId, "failed", new JObject { ["error"] = err.Message });
                throw;
            }
            var assemblyName = buildEnv["assemblyName"]!.Value<string>();
            string manifestStr = await ReadFileInVolume(volume, $"{assemblyName}.btcpay.json");

            PluginManifest manifest;
            try
            {
                manifest = PluginManifest.Parse(manifestStr);
                await UpdateBuild(fullBuildId, "waiting-upload", buildEnv, manifest);
            }
            catch (Exception err)
            {
                await UpdateBuild(fullBuildId, "failed", new JObject { ["error"] = "Invalid plugin manifest: " + err.Message });
                throw;
            }

            await UpdateBuild(fullBuildId, "uploading", null);
            string url;
            try
            {
                url = await AzureStorageClient.Upload(volume, $"{assemblyName}.btcpay", $"{fullBuildId}/{assemblyName}.btcpay");
            }
            catch (Exception err)
            {
                await UpdateBuild(fullBuildId, "failed", new JObject { ["error"] = err.Message });
                throw;
            }
            await UpdateBuild(fullBuildId, "uploaded", new JObject { ["url"] = url });
            await SetVersionBuild(fullBuildId, manifest, buildLogCapture);
        }

        private async Task<BuildInfo> GetBuildInfo(FullBuildId fullBuildId)
        {
            await using var connection = await ConnectionFactory.Open();
            var buildInfo = await connection.QueryFirstOrDefaultAsync<string>("SELECT build_info FROM builds WHERE plugin_slug=@pluginSlug AND id=@buildId",
                new
                {
                    pluginSlug = fullBuildId.PluginSlug.ToString(),
                    buildId = fullBuildId.BuildId
                });
            if (buildInfo is null)
                throw new BuildServiceException("This build doesn't exists");
            return BuildInfo.Parse(buildInfo);
        }

        private async Task SetVersionBuild(FullBuildId fullBuildId, PluginManifest manifest, IOutputCapture buildLogs)
        {
            await using var connection = await ConnectionFactory.Open();
            if (await connection.EnsureIdentifierOwnership(fullBuildId.PluginSlug, manifest.Identifier))
                await connection.SetVersionBuild(fullBuildId, manifest.Version, manifest.BTCPayMinVersion, true);
            else
                buildLogs.AddLine($"The plugin identifier {manifest.Identifier} doesn't belong to this project slug");
        }

        private async Task<string> ReadFileInVolume(string volume, string file)
        {
            var output = new OutputCapture();
            // Let's read the build-env.json
            int code = await ProcessRunner.RunAsync(new ProcessSpec()
            {
                Executable = "docker",
                Arguments = new[] {
                        "run", "--rm", "-v", $"{volume}:/out", "plugin-builder", "cat", $"/out/{file}" },
                OutputCapture = output
            }, default);
            if (code != 0)
                throw new BuildServiceException("docker run to read a file in volume");
            return output.ToString();
        }

        private async Task UpdateBuild(FullBuildId fullBuildId, string newState, JObject? buildInfo, PluginManifest? manifestInfo = null)
        {
            await using var connection = await ConnectionFactory.Open();
            await connection.UpdateBuild(fullBuildId, newState, buildInfo, manifestInfo);
            EventAggregator.Publish(new BuildChanged(fullBuildId, newState)
            {
                BuildInfo = buildInfo?.ToString(),
                ManifestInfo = manifestInfo?.ToString()
            });
        }
    }
}
