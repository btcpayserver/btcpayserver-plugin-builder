using System.Threading.Channels;
using Dapper;
using Newtonsoft.Json.Linq;
using PluginBuilder.Configuration;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.Events;
using PluginBuilder.Util;
using PluginBuilder.Util.Extensions;

using PluginBuilder.Builds;
using PluginBuilder.Builds.BuildBroker;
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
    private readonly IHostApplicationLifetime _lifetime;
    private readonly BuildCancellationRegistry _cancellations;

    public BuildService(
        ILogger<BuildService> logger,
        PluginBuilderOptions options,
        DBConnectionFactory connectionFactory,
        EventAggregator eventAggregator,
        AzureStorageClient azureStorageClient,
        GitHostingProviderFactory providerFactory,
        AdminSettingsCache adminSettingsCache,
        IBuildSandbox buildSandbox,
        BuildExecutorState executorState,
        IHostApplicationLifetime lifetime,
        BuildCancellationRegistry cancellations)
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
        _lifetime = lifetime;
        _cancellations = cancellations;
    }

    public ILogger<BuildService> Logger { get; }
    public DBConnectionFactory ConnectionFactory { get; }
    public EventAggregator EventAggregator { get; }
    public AzureStorageClient AzureStorageClient { get; }

    public Task Build(FullBuildId fullBuildId) => Build(fullBuildId, false);

    public async Task Build(FullBuildId fullBuildId, bool isWhitelisted)
    {
        var cancellation = _cancellations.Register(fullBuildId, _lifetime.ApplicationStopping);
        try
        {
            await Build(fullBuildId, isWhitelisted, cancellation);
        }
        catch (Exception err) when (err is BuildCancelledException || IsAdminCancellation(cancellation))
        {
            // The cancelling request already marked the build failed and recorded why.
            Logger.LogInformation("Build {BuildId} was cancelled by a server admin", fullBuildId);
        }
        finally
        {
            _cancellations.Unregister(fullBuildId, cancellation);
        }
    }

    private bool IsAdminCancellation(CancellationTokenSource cancellation) =>
        cancellation.IsCancellationRequested && !_lifetime.ApplicationStopping.IsCancellationRequested;

    private async Task Build(FullBuildId fullBuildId, bool isWhitelisted, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        // Keep the whitelist exception approved when this build was accepted, even if it is later revoked.
        if (await AlreadyFinished(fullBuildId) || await RejectBuildIfDisabled(fullBuildId, isWhitelisted) ||
            await RejectBuildIfAccountLocked(fullBuildId))
            return;

        await EnsureExecutorAvailable(fullBuildId);
        BuildInfo completedBuildParameters;
        await _semaphore.WaitAsync(token);
        try
        {
            // A build may have been waiting for an execution slot when the setting changed or its account was locked.
            if (await AlreadyFinished(fullBuildId) || await RejectBuildIfDisabled(fullBuildId, isWhitelisted) ||
                await RejectBuildIfAccountLocked(fullBuildId))
                return;
            token.ThrowIfCancellationRequested();

            var buildParameters = await GetBuildInfo(fullBuildId);
            try
            {
                string url;
                PluginManifest manifest;
                bool ownsIdentifier;
                await using (BuildOutputCapture buildLogCapture = new(fullBuildId, ConnectionFactory, Logger))
                {
                    await using (var prepared = await _buildSandbox.PrepareAsync(fullBuildId, buildParameters, token))
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

                        // The verified local artifact no longer depends on broker readiness.
                        await UpdateBuild(fullBuildId, BuildStates.WaitingUpload, staged.BuildEnvironment, manifest);
                        await UpdateBuild(fullBuildId, BuildStates.Uploading, null);
                        url = await AzureStorageClient.UploadStagedArtifact(
                            staged.StagingDirectory,
                            $"{fullBuildId}/{staged.AssemblyName}.btcpay",
                            token);
                    }

                    await using var connection = await ConnectionFactory.Open();
                    ownsIdentifier = await connection.EnsureIdentifierOwnership(fullBuildId.PluginSlug, manifest.Identifier);
                    if (!ownsIdentifier)
                        buildLogCapture.AddLine($"The plugin identifier {manifest.Identifier} doesn't belong to this project slug");
                }

                // Commit the version, URL and successful state together; log persistence is best-effort.
                var publishedInfo = new JObject { ["url"] = url };
                await using (var connection = await ConnectionFactory.Open())
                {
                    await using var transaction = await connection.BeginTransactionAsync();
                    if (ownsIdentifier)
                        await connection.SetVersionBuild(fullBuildId, manifest.Version, manifest.BTCPayMinVersion,
                            manifest.BTCPayMaxVersion, true, transaction);
                    // A cancellation that won the race keeps the build failed and publishes no version.
                    if (!await connection.UpdateUnfinishedBuild(fullBuildId, BuildStates.Uploaded, publishedInfo, tx: transaction))
                    {
                        await transaction.RollbackAsync();
                        throw new BuildCancelledException();
                    }
                    await transaction.CommitAsync();
                }
                EventAggregator.Publish(new BuildChanged(fullBuildId, BuildStates.Uploaded) { BuildInfo = publishedInfo.ToString() });
            }
            catch (Exception err) when (err is BuildCancelledException || IsAdminCancellation(cancellation))
            {
                // Never overwrite the cancellation's recorded reason with a generic failure.
                throw new BuildCancelledException();
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

    /// <summary>An admin may have cancelled the build before this pipeline started or while it waited for a slot.</summary>
    private async Task<bool> AlreadyFinished(FullBuildId fullBuildId)
    {
        await using var connection = await ConnectionFactory.Open();
        var state = await connection.ExecuteScalarAsync<string?>("SELECT state FROM builds WHERE plugin_slug = @pluginSlug AND id = @buildId",
            new { pluginSlug = fullBuildId.PluginSlug.ToString(), buildId = fullBuildId.BuildId });
        return state is not null && BuildStatesExtensions.TerminalEventNames.Contains(state);
    }

    private async Task<bool> RejectBuildIfAccountLocked(FullBuildId fullBuildId)
    {
        await using var connection = await ConnectionFactory.Open();
        var locked = await connection.ExecuteScalarAsync<bool>("""
            SELECT EXISTS(SELECT 1 FROM builds b JOIN "AspNetUsers" u ON u."Id" = b.triggered_by
              WHERE b.plugin_slug = @pluginSlug AND b.id = @buildId
                AND u."LockoutEnabled" AND u."LockoutEnd" > CURRENT_TIMESTAMP)
            """, new { pluginSlug = fullBuildId.PluginSlug.ToString(), buildId = fullBuildId.BuildId });
        if (!locked)
            return false;

        Logger.LogWarning("Skipping build {BuildId} because its account is locked", fullBuildId);
        await UpdateBuild(fullBuildId, BuildStates.Failed,
            new JObject { ["error"] = "Builds by this account are suspended by a server admin." });
        return true;
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
        if (!await connection.UpdateUnfinishedBuild(fullBuildId, newState, buildInfo, manifestInfo))
            throw new BuildCancelledException();
        EventAggregator.Publish(new BuildChanged(fullBuildId, newState) { BuildInfo = buildInfo?.ToString(), ManifestInfo = manifestInfo?.ToString() });
    }

    public async Task<string> FetchIdentifierFromCsprojAsync(string repoUrl, string gitRef, string? pluginDir,
        string? buildConfig)
    {
        repoUrl = BuildPolicy.NormalizeRepositoryUrl(repoUrl);
        // Validate every build input when the build is created; the broker enforces the same rules.
        BuildPolicy.ValidateBuildInputs(gitRef, pluginDir, buildConfig);
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
        private readonly Task _saveTask;
        private readonly ILogger<BuildService> _logger;

        public BuildOutputCapture(FullBuildId fullBuildId, DBConnectionFactory connectionFactory, ILogger<BuildService> logger)
        {
            FullBuildId = fullBuildId;
            ConnectionFactory = connectionFactory;
            _logger = logger;
            _saveTask = SaveLoop();
        }

        private FullBuildId FullBuildId { get; }
        private DBConnectionFactory ConnectionFactory { get; }

        public async ValueTask DisposeAsync()
        {
            lines.Writer.TryComplete();
            try { await _saveTask; }
            catch (Exception error)
            {
                _logger.LogError(error, "Could not persist logs for build {BuildId}; logs may be incomplete", FullBuildId);
            }
        }

        public void AddLine(string line)
        {
            // Broker lines arrive bounded, but the web adds its own, some quoting plugin input.
            if (line.Length > BuildBrokerProtocol.MaximumLogLineCharacters)
                line = line[..BuildBrokerProtocol.MaximumLogLineCharacters];
            // PostgreSQL text rejects NUL; preserve other characters, including tabs.
            lines.Writer.TryWrite(line.Replace("\0", string.Empty));
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

/// <summary>The build finished elsewhere (an admin cancelled it) while this pipeline was still running it.</summary>
public sealed class BuildCancelledException() : Exception("The build was cancelled.");
