using System.Security.Cryptography;
using Dapper;
using Microsoft.Extensions.FileProviders;
using Newtonsoft.Json.Linq;

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
            AzureStorageClient azureStorageClient)
        {
            Logger = logger;
            ProcessRunner = processRunner;
            ConnectionFactory = connectionFactory;
            AzureStorageClient = azureStorageClient;
        }

        public ILogger<BuildService> Logger { get; }
        public ProcessRunner ProcessRunner { get; }
        public DBConnectionFactory ConnectionFactory { get; }
        public AzureStorageClient AzureStorageClient { get; }

        public async Task Build(PluginSlug pluginSlug, PluginBuildParameters buildParameters)
        {
            var fullBuildId = await CreateNewBuild(pluginSlug);
            List<string> args = new List<string>();

            // Create the volumes where the artifacts will be stored
            args.AddRange(new[] { "volume", "create" });
            args.AddRange(new[] { "--label", $"BTCPAY_PLUGIN_BUILD={fullBuildId}" });
            var output = new OutputCapture();
            var code = await ProcessRunner.RunAsync(new ProcessSpec()
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
            if (buildParameters.PluginDirectory != null)
            {
                args.AddRange(new[] { "--env", $"PLUGIN_DIR={buildParameters.PluginDirectory}" });
                info["pluginDir"] = buildParameters.PluginDirectory;
            }
            if (buildParameters.BuildConfig != null)
            {
                args.AddRange(new[] { "--env", $"BUILD_CONFIG={buildParameters.BuildConfig}" });
                info["buildConfig"] = buildParameters.BuildConfig;
            }

            args.AddRange(new[] { "-v", $"{volume}:/out" });
            args.AddRange(new[] { "-ti", "--rm" });
            args.Add("plugin-builder");
            await UpdateBuild(fullBuildId, "running", info);
            JObject buildEnv;
            try
            {
                code = await ProcessRunner.RunAsync(new ProcessSpec()
                {
                    Executable = "docker",
                    Arguments = args.ToArray()
                }, default);
                if (code != 0)
                    throw new BuildServiceException("docker build failed");

                string buildEnvStr = await ReadFileInVolume(volume, "build-env.json");
                buildEnv = JObject.Parse(buildEnvStr);
            }
            catch (Exception err)
            {
                await UpdateBuild(fullBuildId, "failed", new JObject() { ["error"] = err.Message });
                throw;
            }
            var pluginName = buildEnv["pluginName"]!.Value<string>();
            string manifestStr = await ReadFileInVolume(volume, $"{pluginName}.btcpay.json");

            var manifest = JObject.Parse(manifestStr);
            await UpdateBuild(fullBuildId, "waiting-upload", buildEnv, manifest);

            await UpdateBuild(fullBuildId, "uploading", null, null);
            var url = await AzureStorageClient.Upload(volume, $"{pluginName}.btcpay", $"{fullBuildId}/{pluginName}.btcpay");
            await UpdateBuild(fullBuildId, "uploaded", new JObject() { ["url"] = url }, null);
        }

        private async Task<string> ReadFileInVolume(string volume, string file)
        {
            var output = new OutputCapture();
            // Let's read the build-env.json
            int code = await ProcessRunner.RunAsync(new ProcessSpec()
            {
                Executable = "docker",
                Arguments = new[] {
                        "run", "-ti", "--rm", "-v", $"{volume}:/out", "plugin-builder", "cat", $"/out/{file}" },
                OutputCapture = output
            }, default);
            if (code != 0)
                throw new BuildServiceException("docker run to read a file in volume");
            return output.ToString();
        }

        private async Task UpdateBuild(FullBuildId fullBuildId, string newState, JObject? buildInfo, JObject? manifestInfo = null)
        {
            await using var connection = await ConnectionFactory.Open();
            await connection.UpdateBuild(fullBuildId, newState, buildInfo, manifestInfo);
        }

        private async Task<FullBuildId> CreateNewBuild(PluginSlug pluginSlug)
        {
            await using var connection = await ConnectionFactory.Open();
            return new FullBuildId(pluginSlug, await connection.NewBuild(pluginSlug));
        }
    }
}
