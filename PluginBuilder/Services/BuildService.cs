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
        BuildInfo? completedBuildParameters = null;
        await _semaphore.WaitAsync();
        try
        {
            // A build may have been waiting for an execution slot when the setting changed.
            if (await RejectBuildIfDisabled(fullBuildId, isWhitelisted))
                return;
            await EnsureExecutorAvailable(fullBuildId);

            var buildParameters = await GetBuildInfo(fullBuildId);
            await using BuildOutputCapture buildLogCapture = new(fullBuildId, ConnectionFactory);
            try
            {
                await using var prepared = await _buildSandbox.PrepareAsync(fullBuildId, buildParameters);
                JObject runningInfo = new()
                {
                    ["gitRepository"] = buildParameters.GitRepository,
                    ["gitRef"] = buildParameters.GitRef,
                    ["pluginDir"] = buildParameters.PluginDir,
                    ["buildConfig"] = buildParameters.BuildConfig
                };
                await UpdateBuild(fullBuildId, BuildStates.Running, runningInfo);

                // The setting may have changed while sandbox resources were being prepared.
                // Builds without an approved whitelist exception must still honor the flag.
                if (await RejectBuildIfDisabled(fullBuildId, isWhitelisted))
                    return;

                // The remote sandbox returns a private verified download only
                // after the broker has confirmed cleanup of its build resources.
                var staged = await prepared.RunAndStageAsync(new PublishingOutputCapture(
                    buildLogCapture, line => PublishLog(fullBuildId, line)));

                PluginManifest manifest;
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
                var url = await AzureStorageClient.UploadStagedArtifact(
                    staged.StagingDirectory,
                    $"{fullBuildId}/{staged.AssemblyName}.btcpay",
                    uploadCancellation);

                // The broker was cleaned before upload; now discard the private
                // local download before recording successful publication.
                await prepared.DisposeAsync();
                await UpdateBuild(fullBuildId, BuildStates.Uploaded, new JObject { ["url"] = url });
                await SetVersionBuild(fullBuildId, manifest, buildLogCapture);
            }
            catch (Exception err)
            {
                await UpdateBuild(fullBuildId, BuildStates.Failed, new JObject { ["error"] = err.Message });
                throw;
            }

            completedBuildParameters = buildParameters;
        }
        finally
        {
            _semaphore.Release();
        }

        // Contributor metadata is best-effort and should not occupy a scarce build slot.
        if (completedBuildParameters is not null)
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

    private async Task SetVersionBuild(FullBuildId fullBuildId, PluginManifest manifest, IOutputCapture buildLogs)
    {
        await using var connection = await ConnectionFactory.Open();
        if (await connection.EnsureIdentifierOwnership(fullBuildId.PluginSlug, manifest.Identifier))
            await connection.SetVersionBuild(fullBuildId, manifest.Version, manifest.BTCPayMinVersion, manifest.BTCPayMaxVersion, true);
        else
            buildLogs.AddLine($"The plugin identifier {manifest.Identifier} doesn't belong to this project slug");
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
