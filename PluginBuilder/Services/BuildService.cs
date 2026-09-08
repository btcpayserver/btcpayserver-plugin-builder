using System.Threading.Channels;
using Dapper;
using Newtonsoft.Json.Linq;
using PluginBuilder.Configuration;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.Events;
using PluginBuilder.Util;
using PluginBuilder.Util.Extensions;

namespace PluginBuilder.Services;

public class BuildServiceException(string message) : Exception(message);

public class BuildService
{
    private const int MaxBuildMetadataBytes = 1024 * 1024;
    private const int BuildMetadataTooLargeExitCode = 42;
    private const string BuildMetadataReadScript = """
        set -eu
        file="$1"
        limit="$2"
        tmp="$(mktemp)"
        trap 'rm -f "$tmp"' EXIT

        head -c "$((limit + 1))" -- "$file" > "$tmp"
        if [ "$(wc -c < "$tmp")" -gt "$limit" ]; then
            exit 42
        fi

        cat "$tmp"
        """;

    private static readonly TimeSpan BuildMetadataReadTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DockerCleanupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DockerCleanupPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly SemaphoreSlim _semaphore = new(5);
    private readonly GitHostingProviderFactory _providerFactory;
    private readonly PluginBuilderOptions _options;
    private readonly AdminSettingsCache _adminSettingsCache;

    public BuildService(
        ILogger<BuildService> logger,
        PluginBuilderOptions options,
        ProcessRunner processRunner,
        DBConnectionFactory connectionFactory,
        EventAggregator eventAggregator,
        AzureStorageClient azureStorageClient,
        GitHostingProviderFactory providerFactory,
        AdminSettingsCache adminSettingsCache)
    {
        Logger = logger;
        _options = options;
        ProcessRunner = processRunner;
        ConnectionFactory = connectionFactory;
        EventAggregator = eventAggregator;
        AzureStorageClient = azureStorageClient;
        _providerFactory = providerFactory;
        _adminSettingsCache = adminSettingsCache;
    }

    public ILogger<BuildService> Logger { get; }
    public ProcessRunner ProcessRunner { get; }
    public DBConnectionFactory ConnectionFactory { get; }
    public EventAggregator EventAggregator { get; }
    public AzureStorageClient AzureStorageClient { get; }

    public Task Build(FullBuildId fullBuildId) => Build(fullBuildId, false);

    public async Task Build(FullBuildId fullBuildId, bool isWhitelisted)
    {
        // Keep the whitelist exception approved when this build was accepted, even if it is later revoked.
        if (await RejectBuildIfDisabled(fullBuildId, isWhitelisted))
            return;

        BuildInfo buildParameters;
        await _semaphore.WaitAsync();
        try
        {
            // A build may have been waiting for an execution slot when the setting changed.
            if (await RejectBuildIfDisabled(fullBuildId, isWhitelisted))
                return;

            using BuildOutputCapture buildLogCapture = new(fullBuildId, ConnectionFactory);
            List<string> createArgs = new();
            buildParameters = await GetBuildInfo(fullBuildId);
            var containerName = $"plugin-builder-{Guid.NewGuid():N}";
            string volume;
            try
            {
                // Build volumes are owned by a single build and cleaned at the end of this method.
                volume = await CreateBuildVolume(fullBuildId);
            }
            catch (Exception err)
            {
                await UpdateBuild(fullBuildId, BuildStates.Failed, new JObject { ["error"] = err.Message });
                throw;
            }

            try
            {
                try
                {
                    // Then let's build by running our image plugin-builder (built in DockerStartupHostedService)
                    JObject info = new();

                    createArgs.AddRange(["container", "create"]);
                    createArgs.AddRange(new[] { "--name", containerName });
                    createArgs.AddRange(new[] { "--label", $"BTCPAY_PLUGIN_BUILD={fullBuildId}" });
                    createArgs.AddRange(new[] { "--env", $"GIT_REPO={buildParameters.GitRepository}" });
                    info["gitRepository"] = buildParameters.GitRepository;
                    info["dockerVolume"] = volume;
                    if (buildParameters.GitRef != null)
                    {
                        createArgs.AddRange(new[] { "--env", $"GIT_REF={buildParameters.GitRef}" });
                        info["gitRef"] = buildParameters.GitRef;
                    }

                    if (buildParameters.PluginDir != null)
                    {
                        createArgs.AddRange(new[] { "--env", $"PLUGIN_DIR={buildParameters.PluginDir}" });
                        info["pluginDir"] = buildParameters.PluginDir;
                    }

                    if (buildParameters.BuildConfig != null)
                    {
                        createArgs.AddRange(new[] { "--env", $"BUILD_CONFIG={buildParameters.BuildConfig}" });
                        info["buildConfig"] = buildParameters.BuildConfig;
                    }

                    createArgs.AddRange(new[] { "-v", $"{volume}:/out" });
                    createArgs.Add("--rm");
                    createArgs.Add("plugin-builder");
                    OutputCapture createOutput = new();
                    // Let resource creation settle before starting the worker timeout. Cancelling
                    // this call can leave us unable to tell whether Docker created the container.
                    var createCode = await ProcessRunner.RunAsync(new ProcessSpec
                    {
                        Executable = "docker",
                        Arguments = createArgs.ToArray(),
                        OutputCapture = createOutput,
                        ErrorCapture = buildLogCapture
                    }, CancellationToken.None);
                    if (createCode != 0)
                        throw new BuildServiceException("docker container create failed");

                    await UpdateBuild(fullBuildId, BuildStates.Running, info);

                    // The setting may have changed while Docker resources were being created.
                    // Builds without an approved whitelist exception must still honor the flag.
                    if (await RejectBuildIfDisabled(fullBuildId, isWhitelisted))
                    {
                        if (!await ForceRemoveBuildContainer(containerName))
                            throw new BuildServiceException(
                                "Plugin builds were disabled and the build container could not be removed");
                        return;
                    }
                }
                catch (Exception err)
                {
                    await ForceRemoveBuildContainer(containerName);
                    await UpdateBuild(fullBuildId, BuildStates.Failed, new JObject { ["error"] = err.Message });
                    throw;
                }

                JObject buildEnv;
                try
                {
                    int code;
                    using var timeout = new CancellationTokenSource(_options.BuildTimeout);
                    try
                    {
                        code = await ProcessRunner.RunAsync(new ProcessSpec
                        {
                            Executable = "docker",
                            Arguments = ["container", "start", "--attach", containerName],
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
                        }, timeout.Token);
                    }
                    catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                    {
                        if (!await ForceRemoveBuildContainer(containerName))
                            throw new BuildServiceException("Plugin build timed out and its container could not be removed");
                        throw new BuildServiceException($"Plugin build timed out after {_options.BuildTimeout}");
                    }
                    catch
                    {
                        await ForceRemoveBuildContainer(containerName);
                        throw;
                    }

                    if (code != 0)
                    {
                        await ForceRemoveBuildContainer(containerName);
                        throw new BuildServiceException("docker build failed");
                    }

                    var buildEnvStr = await ReadFileInVolume(volume, "build-env.json");
                    buildEnv = JObject.Parse(buildEnvStr);
                }
                catch (Exception err)
                {
                    await UpdateBuild(fullBuildId, BuildStates.Failed, new JObject { ["error"] = err.Message });
                    throw;
                }

                string assemblyName;
                PluginManifest manifest;
                try
                {
                    assemblyName = buildEnv["assemblyName"]?.Value<string>()
                        ?? throw new BuildServiceException("build-env.json missing assemblyName");
                    var manifestStr = await ReadFileInVolume(volume, $"{assemblyName}.btcpay.json");
                    manifest = PluginManifest.Parse(manifestStr, strictBTCPayVersionCondition: true);
                    await UpdateBuild(fullBuildId, BuildStates.WaitingUpload, buildEnv, manifest);
                }
                catch (Exception err)
                {
                    await UpdateBuild(fullBuildId, BuildStates.Failed,
                        new JObject { ["error"] = "Failed to read or parse plugin manifest: " + err.Message });
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
                await RemoveBuildVolume(volume);
            }
        }
        finally
        {
            _semaphore.Release();
        }

        await SavePluginContributorSnapshot(fullBuildId.PluginSlug, buildParameters);
    }

    private async Task<bool> RejectBuildIfDisabled(FullBuildId fullBuildId, bool isWhitelisted)
    {
        if (_adminSettingsCache.NewBuildsEnabled || isWhitelisted)
            return false;

        Logger.LogWarning("Skipping build {BuildId} because plugin builds are disabled", fullBuildId);
        await UpdateBuild(fullBuildId, BuildStates.Failed,
            new JObject { ["error"] = "Plugin builds are temporarily disabled." });
        return true;
    }

    private async Task<string> CreateBuildVolume(FullBuildId fullBuildId)
    {
        var volume = $"plugin-builder-volume-{Guid.NewGuid():N}";
        int code;
        try
        {
            code = await ProcessRunner.RunAsync(
                new ProcessSpec
                {
                    Executable = "docker",
                    Arguments = ["volume", "create", "--label", $"BTCPAY_PLUGIN_BUILD={fullBuildId}", volume]
                },
                CancellationToken.None);
        }
        catch
        {
            await RemoveBuildVolume(volume);
            throw;
        }

        if (code != 0)
        {
            await RemoveBuildVolume(volume);
            throw new BuildServiceException("docker volume create failed");
        }

        return volume;
    }

    private async Task RemoveBuildVolume(string volume)
    {
        OutputCapture error = new();
        using var timeout = new CancellationTokenSource(DockerCleanupTimeout);
        int code;
        try
        {
            code = await ProcessRunner.RunAsync(new ProcessSpec
            {
                Executable = "docker",
                Arguments = ["volume", "rm", volume],
                ErrorCapture = error
            }, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            Logger.LogCritical("Timed out while removing docker build volume {Volume}", volume);
            return;
        }

        if (code != 0)
        {
            var details = error.ToString().Trim();
            if (string.IsNullOrEmpty(details))
                Logger.LogWarning("Failed to remove docker build volume {Volume}", volume);
            else
                Logger.LogWarning("Failed to remove docker build volume {Volume}: {Error}", volume, details);
        }
    }

    private async Task<bool> ForceRemoveBuildContainer(string containerName)
    {
        using var timeout = new CancellationTokenSource(DockerCleanupTimeout);
        try
        {
            OutputCapture error = new();
            var code = await ProcessRunner.RunAsync(new ProcessSpec
            {
                Executable = "docker",
                Arguments = ["container", "rm", "--force", containerName],
                ErrorCapture = error
            }, timeout.Token);

            if (code == 0)
                return true;

            var details = error.ToString();
            if (details.Contains("No such container", StringComparison.OrdinalIgnoreCase))
                return true;

            if (details.Contains("removal of container", StringComparison.OrdinalIgnoreCase) &&
                details.Contains("already in progress", StringComparison.OrdinalIgnoreCase))
                return await WaitForContainerRemoval(containerName, timeout.Token);

            Logger.LogCritical("Failed to force-remove plugin build container {ContainerName}: {Error}",
                containerName, details.Trim());
            return false;
        }
        catch (OperationCanceledException)
        {
            Logger.LogCritical("Timed out while force-removing plugin build container {ContainerName}", containerName);
            return false;
        }
    }

    private async Task<bool> WaitForContainerRemoval(string containerName, CancellationToken cancellationToken)
    {
        while (true)
        {
            OutputCapture error = new();
            var code = await ProcessRunner.RunAsync(new ProcessSpec
            {
                Executable = "docker",
                Arguments = ["container", "inspect", containerName],
                ErrorCapture = error
            }, cancellationToken);

            if (code != 0)
                return error.ToString().Contains("No such container", StringComparison.OrdinalIgnoreCase);

            await Task.Delay(DockerCleanupPollInterval, cancellationToken);
        }
    }

    private async Task SavePluginContributorSnapshot(PluginSlug pluginSlug, BuildInfo buildInfo)
    {
        try
        {
            var provider = _providerFactory.GetProvider(buildInfo.GitRepository);
            if (provider == null)
                return;
            var contributors = await provider.GetContributorsAsync(buildInfo.GitRepository, buildInfo.PluginDir);
            await GithubService.SaveSnapshot(_options.PluginDataDir, pluginSlug, contributors);
        }
        catch (Exception) { }
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
            await connection.SetVersionBuild(fullBuildId, manifest.Version, manifest.BTCPayMinVersion, manifest.BTCPayMaxVersion, true);
        else
            buildLogs.AddLine($"The plugin identifier {manifest.Identifier} doesn't belong to this project slug");
    }

    private async Task<string> ReadFileInVolume(string volume, string file)
    {
        var containerName = $"plugin-builder-metadata-{Guid.NewGuid():N}";
        try
        {
            var createCode = await ProcessRunner.RunAsync(
                new ProcessSpec
                {
                    Executable = "docker",
                    Arguments =
                    [
                        "container", "create",
                        "--name", containerName,
                        "--rm",
                        "-v", $"{volume}:/out:ro",
                        "plugin-builder",
                        "/bin/sh", "-c", BuildMetadataReadScript,
                        "read-build-metadata",
                        $"/out/{file}",
                        MaxBuildMetadataBytes.ToString()
                    ],
                    OutputCapture = new OutputCapture(),
                    ErrorCapture = new OutputCapture()
                },
                CancellationToken.None);

            if (createCode != 0)
                throw new BuildServiceException(
                    $"docker container create failed while reading build metadata file '{file}'");
        }
        catch
        {
            await ForceRemoveBuildContainer(containerName);
            throw;
        }

        OutputCapture output = new();
        using var timeout = new CancellationTokenSource(BuildMetadataReadTimeout);
        try
        {
            var code = await ProcessRunner.RunAsync(
                new ProcessSpec
                {
                    Executable = "docker",
                    Arguments = ["container", "start", "--attach", containerName],
                    OutputCapture = output,
                    ErrorCapture = new OutputCapture()
                },
                timeout.Token);

            if (code == BuildMetadataTooLargeExitCode)
                throw new BuildServiceException(
                    $"Build metadata file '{file}' exceeds the {MaxBuildMetadataBytes}-byte limit");

            if (code != 0)
                throw new BuildServiceException(
                    $"docker container start failed while reading build metadata file '{file}'");

            return output.ToString();
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            await ForceRemoveBuildContainer(containerName);
            throw new BuildServiceException($"Timed out while reading build metadata file '{file}'");
        }
        catch
        {
            await ForceRemoveBuildContainer(containerName);
            throw;
        }
    }

    public async Task UpdateBuild(FullBuildId fullBuildId, BuildStates newState, JObject? buildInfo, PluginManifest? manifestInfo = null)
    {
        await using var connection = await ConnectionFactory.Open();
        await connection.UpdateBuild(fullBuildId, newState, buildInfo, manifestInfo);
        EventAggregator.Publish(new BuildChanged(fullBuildId, newState) { BuildInfo = buildInfo?.ToString(), ManifestInfo = manifestInfo?.ToString() });
    }

    public async Task<string> FetchIdentifierFromCsprojAsync(string repoUrl, string gitRef, string? pluginDir = null)
    {
        var provider = _providerFactory.GetProvider(repoUrl);
        if (provider == null)
            throw new BuildServiceException("Unsupported git hosting provider. Supported: GitHub, GitLab.");
        return await provider.FetchIdentifierFromCsprojAsync(repoUrl, gitRef, pluginDir);
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
