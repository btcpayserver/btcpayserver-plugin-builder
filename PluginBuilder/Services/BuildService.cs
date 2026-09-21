using System.Threading.Channels;
using System.Text;
using Dapper;
using Newtonsoft.Json.Linq;
using PluginBuilder.Configuration;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.Events;
using PluginBuilder.Util;
using PluginBuilder.Util.Extensions;

using PluginBuilder.Builds;
using PluginBuilder.Builds.Services;

namespace PluginBuilder.Services;

public class BuildService
{
    private static readonly SemaphoreSlim _semaphore = new(BuildPolicy.MaxConcurrentBuilds);
    private readonly GitHostingProviderFactory _providerFactory;
    private readonly PluginBuilderOptions _options;
    private readonly AdminSettingsCache _adminSettingsCache;
    private readonly IBuildSandbox _buildSandbox;
    private readonly BuildExecutorState _executorState;

    public BuildService(
        ILogger<BuildService> logger,
        PluginBuilderOptions options,
        DBConnectionFactory connectionFactory,
        EventAggregator eventAggregator,
        AzureStorageClient azureStorageClient,
        GitHostingProviderFactory providerFactory,
        AdminSettingsCache adminSettingsCache,
        IBuildSandbox buildSandbox,
        BuildExecutorState executorState)
    {
        Logger = logger;
        _options = options;
        ConnectionFactory = connectionFactory;
        EventAggregator = eventAggregator;
        AzureStorageClient = azureStorageClient;
        _providerFactory = providerFactory;
        _adminSettingsCache = adminSettingsCache;
        _buildSandbox = buildSandbox;
        _executorState = executorState;
    }

    public ILogger<BuildService> Logger { get; }
    public DBConnectionFactory ConnectionFactory { get; }
    public EventAggregator EventAggregator { get; }
    public AzureStorageClient AzureStorageClient { get; }

    public Task Build(FullBuildId fullBuildId) => Build(fullBuildId, false);

    public async Task Build(FullBuildId fullBuildId, bool isWhitelisted)
    {
        // Keep the whitelist exception approved when this build was accepted, even if it is later revoked.
        if (await RejectBuildIfDisabled(fullBuildId, isWhitelisted))
            return;

        await EnsureExecutorAvailable(fullBuildId);
        BuildInfo completedBuildParameters;
        await _semaphore.WaitAsync();
        try
        {
            // A build may have been waiting for an execution slot when the setting changed.
            if (await RejectBuildIfDisabled(fullBuildId, isWhitelisted))
                return;
            await EnsureExecutorAvailable(fullBuildId);

            var buildParameters = await GetBuildInfo(fullBuildId);
            try
            {
                string url;
                PluginManifest manifest;
                bool ownsIdentifier;
                await using (BuildOutputCapture buildLogCapture = new(fullBuildId, ConnectionFactory))
                {
                    await using (var prepared = await _buildSandbox.PrepareAsync(fullBuildId, buildParameters))
                    {
                        JObject runningInfo = new()
                        {
                            ["gitRepository"] = buildParameters.GitRepository,
                            ["gitRef"] = buildParameters.GitRef,
                            ["pluginDir"] = buildParameters.PluginDir,
                            ["buildConfig"] = buildParameters.BuildConfig
                        };
                        await UpdateBuild(fullBuildId, BuildStates.Running, runningInfo);

                        // The broker exposes results only after sandbox cleanup; the client verifies the download.
                        var staged = await prepared.RunAndStageAsync(new PublishingOutputCapture(
                            buildLogCapture, line => PublishLog(fullBuildId, line)));
                        try
                        {
                            manifest = PluginManifest.Parse(staged.ManifestJson, strictBTCPayVersionCondition: true);
                        }
                        catch (Exception err)
                        {
                            throw new BuildServiceException("Failed to parse plugin manifest: " + err.Message);
                        }

                        var uploadCancellation = _executorState.StopToken;
                        if (!_executorState.Snapshot.IsReady || uploadCancellation.IsCancellationRequested)
                            throw new BuildServiceException("The isolated build executor was stopped.");

                        await UpdateBuild(fullBuildId, BuildStates.WaitingUpload, staged.BuildEnvironment, manifest);
                        await UpdateBuild(fullBuildId, BuildStates.Uploading, null);
                        url = await AzureStorageClient.UploadStagedArtifact(
                            staged.StagingDirectory,
                            $"{fullBuildId}/{staged.AssemblyName}.btcpay",
                            uploadCancellation);
                    }

                    await using var connection = await ConnectionFactory.Open();
                    ownsIdentifier = await connection.EnsureIdentifierOwnership(fullBuildId.PluginSlug, manifest.Identifier);
                    if (!ownsIdentifier)
                        buildLogCapture.AddLine($"The plugin identifier {manifest.Identifier} doesn't belong to this project slug");
                }

                // Logs are persisted before exposing the version. Commit its URL and state together.
                var publishedInfo = new JObject { ["url"] = url };
                await using (var connection = await ConnectionFactory.Open())
                {
                    await using var transaction = await connection.BeginTransactionAsync();
                    if (ownsIdentifier)
                        await connection.SetVersionBuild(fullBuildId, manifest.Version, manifest.BTCPayMinVersion,
                            manifest.BTCPayMaxVersion, true, transaction);
                    await connection.UpdateBuild(fullBuildId, BuildStates.Uploaded, publishedInfo, tx: transaction);
                    await transaction.CommitAsync();
                }
                EventAggregator.Publish(new BuildChanged(fullBuildId, BuildStates.Uploaded) { BuildInfo = publishedInfo.ToString() });
            }
            catch (Exception err)
            {
                // The build page renders this text, so only exceptions with deliberately
                // user-safe messages may reach it. Callers discard the returned task, so
                // unexpected failures must be logged here or they are lost.
                var safe = err is BuildServiceException or AzureStorageClientException;
                if (!safe)
                    Logger.LogError(err, "Build {BuildId} failed", fullBuildId);
                await UpdateBuild(fullBuildId, BuildStates.Failed,
                    new JObject { ["error"] = safe ? err.Message : "Plugin build failed." });
                throw;
            }

            completedBuildParameters = buildParameters;
        }
        finally
        {
            _semaphore.Release();
        }

        // Contributor metadata is best-effort and should not occupy a scarce build slot.
        await SavePluginContributorSnapshot(fullBuildId.PluginSlug, completedBuildParameters);
    }

    private void PublishLog(FullBuildId fullBuildId, string? line)
    {
        if (!string.IsNullOrEmpty(line))
            EventAggregator.Publish(new BuildLogUpdated(fullBuildId, line));
    }

    private async Task EnsureExecutorAvailable(FullBuildId fullBuildId)
    {
        if (_executorState.Snapshot.IsReady)
            return;

        const string error = "The isolated build executor is temporarily unavailable.";
        await UpdateBuild(fullBuildId, BuildStates.Failed, new JObject { ["error"] = error });
        throw new BuildServiceException(error);
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

    public async Task UpdateBuild(FullBuildId fullBuildId, BuildStates newState, JObject? buildInfo, PluginManifest? manifestInfo = null)
    {
        await using var connection = await ConnectionFactory.Open();
        await connection.UpdateBuild(fullBuildId, newState, buildInfo, manifestInfo);
        EventAggregator.Publish(new BuildChanged(fullBuildId, newState) { BuildInfo = buildInfo?.ToString(), ManifestInfo = manifestInfo?.ToString() });
    }

    public async Task<string> FetchIdentifierFromCsprojAsync(string repoUrl, string gitRef, string? pluginDir = null)
    {
        repoUrl = BuildPolicy.NormalizeRepositoryUrl(repoUrl);
        BuildPolicy.ValidateBuildInputs(gitRef, pluginDir, buildConfig: null);
        var provider = _providerFactory.GetProvider(repoUrl);
        if (provider == null)
            throw new BuildServiceException("Unsupported git hosting provider. Supported: GitHub, GitLab.");
        return await provider.FetchIdentifierFromCsprojAsync(repoUrl, gitRef, pluginDir);
    }


    private sealed class PublishingOutputCapture(IOutputCapture capture, Action<string> publish) : IOutputCapture
    {
        public void AddLine(string line)
        {
            capture.AddLine(line);
            publish(line);
        }
    }

    public class BuildOutputCapture : IOutputCapture, IAsyncDisposable
    {
        private readonly Channel<string> lines = Channel.CreateUnbounded<string>();
        private readonly object _gate = new();
        private readonly Task _saveTask;
        private int _lineCount;
        private int _byteCount;

        public BuildOutputCapture(FullBuildId fullBuildId, DBConnectionFactory connectionFactory)
        {
            FullBuildId = fullBuildId;
            ConnectionFactory = connectionFactory;
            _saveTask = SaveLoop();
        }

        private FullBuildId FullBuildId { get; }
        private DBConnectionFactory ConnectionFactory { get; }

        public async ValueTask DisposeAsync()
        {
            lines.Writer.TryComplete();
            await _saveTask;
        }

        public void AddLine(string line)
        {
            if (line.Length > BuildPolicy.MaxBuildLogLineBytes)
                line = line[..BuildPolicy.MaxBuildLogLineBytes];

            var bytes = Encoding.UTF8.GetByteCount(line) + 1;
            lock (_gate)
            {
                if (_lineCount >= BuildPolicy.MaxBuildLogLines ||
                    _byteCount + bytes > BuildPolicy.MaxBuildLogBytes)
                    return;

                _lineCount++;
                _byteCount += bytes;
                lines.Writer.TryWrite(line);
            }
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
