using System.Threading.Channels;
using Dapper;
using Newtonsoft.Json.Linq;
using PluginBuilder.Events;
using PluginBuilder.Util;
using PluginBuilder.Util.Extensions;

namespace PluginBuilder.Services;

public class BuildServiceException(string message) : Exception(message);

public class BuildService
{
    private static readonly SemaphoreSlim _semaphore = new(5);

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
        await _semaphore.WaitAsync();
        try
        {
            using BuildOutputCapture buildLogCapture = new(fullBuildId, ConnectionFactory);
            List<string> args = new();
            var buildParameters = await GetBuildInfo(fullBuildId);
            // Create the volumes where the artifacts will be stored
            args.AddRange(new[] { "volume", "create" });
            args.AddRange(new[] { "--label", $"BTCPAY_PLUGIN_BUILD={fullBuildId}" });
            int code;
            string volume;
            try
            {
                OutputCapture output = new();
                code = await ProcessRunner.RunAsync(
                    new ProcessSpec
                    {
                        Executable = "docker",
                        Arguments = args.ToArray(),
                        OutputCapture = output
                    },
                    default);
                if (code != 0)
                    throw new BuildServiceException("docker volume create failed");
                volume = output.ToString().Trim();
                args.Clear();

                // Then let's build by running our image plugin-builder (built in DockerStartupHostedService)
                JObject info = new();

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
                await UpdateBuild(fullBuildId, BuildStates.Running, info);
            }
            catch (Exception err)
            {
                await UpdateBuild(fullBuildId, BuildStates.Failed, new JObject { ["error"] = err.Message });
                throw;
            }

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

                var buildEnvStr = await ReadFileInVolume(volume, "build-env.json");
                buildEnv = JObject.Parse(buildEnvStr);
            }
            catch (Exception err)
            {
                await UpdateBuild(fullBuildId, BuildStates.Failed, new JObject { ["error"] = err.Message });
                throw;
            }

            var assemblyName = buildEnv["assemblyName"]!.Value<string>();
            var manifestStr = await ReadFileInVolume(volume, $"{assemblyName}.btcpay.json");

            PluginManifest manifest;
            try
            {
                manifest = PluginManifest.Parse(manifestStr);
                await UpdateBuild(fullBuildId, BuildStates.WaitingUpload, buildEnv, manifest);
            }
            catch (Exception err)
            {
                await UpdateBuild(fullBuildId, BuildStates.Failed,
                    new JObject { ["error"] = "Invalid plugin manifest: " + err.Message });
                throw;
            }

            await UpdateBuild(fullBuildId, BuildStates.Uploading, null);
            string url;
            try
            {
                url = await AzureStorageClient.Upload(volume, $"{assemblyName}.btcpay",
                    $"{fullBuildId}/{assemblyName}.btcpay");
            }
            catch (Exception err)
            {
                await UpdateBuild(fullBuildId, BuildStates.Failed, new JObject { ["error"] = err.Message });
                throw;
            }

            await UpdateBuild(fullBuildId, BuildStates.Uploaded, new JObject { ["url"] = url });
            await SetVersionBuild(fullBuildId, manifest, buildLogCapture);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task<BuildInfo> GetBuildInfo(FullBuildId fullBuildId)
    {
        await using var connection = await ConnectionFactory.Open();
        var buildInfo = await connection.QueryFirstOrDefaultAsync<string>("SELECT build_info FROM builds WHERE plugin_slug=@pluginSlug AND id=@buildId",
            new { pluginSlug = fullBuildId.PluginSlug.ToString(), buildId = fullBuildId.BuildId });
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
        OutputCapture output = new();
        // Let's read the build-env.json
        var code = await ProcessRunner.RunAsync(
            new ProcessSpec
            {
                Executable = "docker",
                Arguments = new[] { "run", "--rm", "-v", $"{volume}:/out", "plugin-builder", "cat", $"/out/{file}" },
                OutputCapture = output
            }, default);
        if (code != 0)
            throw new BuildServiceException("docker run to read a file in volume");
        return output.ToString();
    }

    public async Task UpdateBuild(FullBuildId fullBuildId, BuildStates newState, JObject? buildInfo, PluginManifest? manifestInfo = null)
    {
        await using var connection = await ConnectionFactory.Open();
        await connection.UpdateBuild(fullBuildId, newState, buildInfo, manifestInfo);
        EventAggregator.Publish(new BuildChanged(fullBuildId, newState) { BuildInfo = buildInfo?.ToString(), ManifestInfo = manifestInfo?.ToString() });
    }
    
    
    public class BuildOutputCapture : IOutputCapture, IDisposable
    {
        private readonly Channel<string> lines = Channel.CreateUnbounded<string>();

        public BuildOutputCapture(FullBuildId fullBuildId, DBConnectionFactory connectionFactory)
        {
            FullBuildId = fullBuildId;
            ConnectionFactory = connectionFactory;
            _ = SaveLoop();
        }

        private FullBuildId FullBuildId { get; }
        private DBConnectionFactory ConnectionFactory { get; }

        public void Dispose()
        {
            lines.Writer.TryComplete();
        }

        public void AddLine(string line)
        {
            lines.Writer.TryWrite(line);
        }

        private async Task SaveLoop()
        {
            while (await lines.Reader.WaitToReadAsync())
            {
                List<string> rows = new();
                while (lines.Reader.TryRead(out var l))
                    rows.Add(l);
                await using var conn = await ConnectionFactory.Open();
                await conn.ExecuteAsync("INSERT INTO builds_logs VALUES (@pluginSlug, @buildId, @log)",
                    rows.Select(row =>
                        new
                        {
                            pluginSlug = FullBuildId.PluginSlug.ToString(),
                            buildId = FullBuildId.BuildId,
                            log = row
                        }).ToArray());
            }
        }
    }
}
