using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PluginBuilder.BuildBroker.Services;
using PluginBuilder.Builds;
using PluginBuilder.Builds.BuildBroker;
using PluginBuilder.Builds.Services;
using PluginBuilder.Util;

namespace PluginBuilder.BuildBroker;

public sealed class BrokerRequestException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>Owns admission, lifetime and cleanup. No Docker arguments cross this boundary.</summary>
public sealed class BrokerCoordinator(
    IBuildSandbox sandbox, BuildExecutorState executor, BuildBrokerSettings settings,
    ILogger<BrokerCoordinator> logger) : BackgroundService
{
    public const long MaximumArtifactBytes = BuildBrokerProtocol.MaximumArtifactBytes;
    private readonly object _gate = new();
    private readonly Dictionary<string, Lease> _leases = new(StringComparer.Ordinal);
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private bool _stopping;
    public string InstanceId => _instanceId;

    public BrokerStatus Status()
    {
        var snapshot = executor.Snapshot;
        return new(snapshot.IsReady && !_stopping, snapshot.WorkerImageId, snapshot.ProxyImageId,
            snapshot.IsReady ? null : "The isolated build executor is unavailable.", _instanceId);
    }

    public static (FullBuildId, BuildInfo) Validate(BrokerBuildRequest request)
    {
        // Validate before URI canonicalization, which could otherwise erase traversal.
        if (request.PluginSlug is null || request.PluginSlug.Any(char.IsControl) ||
            !PluginSlug.TryParse(request.PluginSlug, out var slug) || request.BuildId < 0)
            throw new BrokerRequestException(400, "Invalid build identifier.");
        if (request.GitRepository is null || request.GitRepository.Length > 2048 ||
            request.GitRepository.Any(char.IsControl) || request.GitRepository.Contains('\\') ||
            request.GitRepository.Contains('%') ||
            request.GitRepository.Split('/').Any(s => s is "." or ".."))
            throw new BrokerRequestException(400, "Invalid repository URL.");
        try
        {
            var repository = BuildPolicy.NormalizeRepositoryUrl(request.GitRepository);
            BuildPolicy.ValidateBuildInputs(request.GitRef, request.PluginDir, request.BuildConfig);
            return (new FullBuildId(slug, request.BuildId), new BuildInfo
            {
                GitRepository = repository, GitRef = request.GitRef, PluginDir = request.PluginDir,
                BuildConfig = string.IsNullOrEmpty(request.BuildConfig) ? "Release" : request.BuildConfig
            });
        }
        catch (BuildServiceException) { throw new BrokerRequestException(400, "Invalid build parameters."); }
    }

    public BrokerBuildAccepted Submit(BrokerBuildRequest request)
    {
        var (id, info) = Validate(request);
        lock (_gate)
        {
            if (_stopping || !executor.Snapshot.IsReady)
                throw new BrokerRequestException(503, "The isolated build executor is unavailable.");
            if (_leases.Count >= BuildPolicy.MaxConcurrentBuilds)
                throw new BrokerRequestException(429, "The isolated build queue is full.");
            // A compromised web app cannot remove the broker's own admission limit.
            var lease = new Lease(executor.StopToken, settings.LeaseLifetime);
            _leases.Add(lease.Id, lease);
            lease.Work = Task.Run(() => RunAsync(lease, id, info));
            return new(lease.Id);
        }
    }

    public BrokerBuildStatus ReadStatus(string id, int cursor)
    {
        var lease = Find(id);
        BrokerBuildStatus status;
        bool releaseFailure;
        lock (lease.Gate)
        {
            if (cursor < 0 || cursor > lease.Logs.Count)
                throw new BrokerRequestException(400, "Invalid log cursor.");
            List<string> chunk = [];
            var size = 0;
            for (var index = cursor; index < lease.Logs.Count; index++)
            {
                var bytes = Encoding.UTF8.GetByteCount(lease.Logs[index]) + 1;
                if (size + bytes > BuildBrokerProtocol.MaximumLogPageBytes) break;
                size += bytes;
                chunk.Add(lease.Logs[index]);
            }
            var next = cursor + chunk.Count;
            // Do not signal a terminal result until the client has consumed its logs.
            var state = next < lease.Logs.Count ? "running" : lease.State;
            status = new(state, next, chunk.ToArray(), state == "succeeded" ? lease.Result : null,
                state == "failed" ? lease.Error : null);
            releaseFailure = state == "failed" && lease.Cleaned && !lease.CleanupFailed;
        }
        // Returning the final failure consumes it, just as downloading consumes a successful result.
        // Cleanup already completed; no client cleanup request is needed to admit a retry.
        if (!releaseFailure)
            return status;
        lock (_gate) _leases.Remove(id);
        lease.DisposeCancellation();
        return status;
    }

    private async Task RunAsync(Lease lease, FullBuildId id, BuildInfo info)
    {
        var preparationCompleted = false;
        try
        {
            lease.Prepared = await sandbox.PrepareAsync(id, info, lease.Token);
            preparationCompleted = true;
            lease.Token.ThrowIfCancellationRequested();
            lock (lease.Gate) lease.State = "running";
            var staged = await lease.Prepared.RunAndStageAsync(lease);
            lease.Token.ThrowIfCancellationRequested();
            var environment = staged.BuildEnvironment.ToString(Formatting.None);
            var hash = staged.BuildEnvironment["buildHash"]?.Value<string>();
            if (Encoding.UTF8.GetByteCount(environment) > BuildPolicy.MaxBuildMetadataBytes ||
                Encoding.UTF8.GetByteCount(staged.ManifestJson) > BuildPolicy.MaxBuildMetadataBytes ||
                !BuildPolicy.IsSafeAssemblyName(staged.AssemblyName) || !BuildPolicy.IsSha256Hex(hash))
                throw new InvalidOperationException("Invalid staged metadata.");
            // Copy into a broker-only, bounded anonymous file, never mounted into a sandbox.
            // Unlink immediately: the open handle survives sandbox cleanup, but not a broker crash.
            var resultPath = Path.Combine(Path.GetTempPath(), $"pb-result-{Guid.NewGuid():N}");
            var resultOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.Read | FileShare.Delete,
                Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose
            };
            if (!OperatingSystem.IsWindows())
                resultOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            lease.Artifact = new FileStream(resultPath, resultOptions);
            File.Delete(resultPath);
            long length;
            // The trusted stager and all writers have stopped before this open.
            await using (var input = DockerBuildSandbox.OpenStagedFile(staged.StagingDirectory, "artifact.btcpay", MaximumArtifactBytes, 81920))
            {
                length = input.Length;
                var buffer = new byte[81920];
                long remaining = length;
                while (remaining > 0)
                {
                    var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), lease.Token);
                    if (count == 0) throw new IOException("Incomplete staged artifact.");
                    await lease.Artifact.WriteAsync(buffer.AsMemory(0, count), lease.Token);
                    remaining -= count;
                }
                if (await input.ReadAsync(buffer.AsMemory(0, 1), lease.Token) != 0)
                    throw new InvalidOperationException("Staged artifact grew.");
            }
            lease.Artifact.Position = 0;
            var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(lease.Artifact, lease.Token));
            if (digest != hash || lease.Artifact.Length != length)
                throw new InvalidOperationException("Invalid staged artifact digest.");
            lease.Artifact.Position = 0;
            // Completion is the cleanup acknowledgement; the website does not coordinate disposal.
            try { await lease.Prepared.DisposeAsync(); }
            catch
            {
                lease.CleanupFailed = true;
                executor.MarkUnavailable("Broker cleanup could not be confirmed.");
                throw;
            }
            lease.Prepared = null;
            lease.Token.ThrowIfCancellationRequested();
            lock (lease.Gate)
            {
                lease.Result = new(environment, staged.ManifestJson, staged.AssemblyName, lease.Artifact.Length, hash);
                lease.State = "succeeded";
            }
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "Broker build {LeaseId} failed", lease.Id);
            // Prepare performs its own partial-resource cleanup before throwing.
            // If it could not prove cleanup, it marks the executor unavailable and
            // returns no prepared handle: never treat that as confirmed cleanup.
            if (!preparationCompleted && lease.ExecutorGeneration.IsCancellationRequested)
                lease.CleanupFailed = true;
            // Only explicitly public diagnostics cross this boundary. Other exception
            // messages may contain internal paths, credentials or process output.
            var message = error switch
            {
                PublicBuildException e => e.Message,
                OperationCanceledException => "The isolated build was cancelled or its lease expired.",
                _ => "Plugin build failed in the isolated executor."
            };
            try { await CleanupAsync(lease); }
            catch (Exception cleanupError) { logger.LogError(cleanupError, "Broker cleanup failed for {LeaseId}", lease.Id); }
            lock (lease.Gate) { lease.Error = message; lease.State = "failed"; }
        }
    }

    public async Task WriteArtifactAsync(string id, HttpContext context)
    {
        var lease = Find(id);
        using var transferTimeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lease.Token);
        transferTimeout.CancelAfter(TimeSpan.FromMinutes(5));
        await lease.ArtifactGate.WaitAsync(transferTimeout.Token);
        try
        {
            BrokerBuildResult result;
            lock (lease.Gate)
            {
                if (lease.State != "succeeded" || lease.Result is null || lease.Cleaned || lease.CleanupFailed)
                    throw new BrokerRequestException(409, "No completed artifact is available.");
                result = lease.Result;
            }
            var artifact = lease.Artifact ?? throw new BrokerRequestException(409, "No completed artifact is available.");
            artifact.Position = 0;
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(artifact, transferTimeout.Token));
            if (artifact.Length != result.ArtifactLength || hash != result.ArtifactSha256)
            {
                executor.MarkUnavailable("Staged artifact integrity failed.");
                throw new BrokerRequestException(503, "Staged artifact integrity failed.");
            }
            artifact.Position = 0;
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength = result.ArtifactLength;
            var buffer = new byte[81920];
            var remaining = result.ArtifactLength;
            while (remaining > 0)
            {
                var count = await artifact.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), transferTimeout.Token);
                if (count == 0) throw new IOException("Incomplete staged artifact.");
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, count), transferTimeout.Token);
                remaining -= count;
            }
        }
        finally { lease.ArtifactGate.Release(); }
        // A completed transfer releases the retained result and its admission slot.
        await ReleaseLeaseAsync(id);
    }

    private async Task ReleaseLeaseAsync(string id)
    {
        RequireId(id);
        Lease? lease;
        lock (_gate) _leases.TryGetValue(id, out lease);
        if (lease is null) return;
        // Do not tie cleanup to RequestAborted: disconnected clients cannot retain a writer.
        lease.Cancel();
        // Scratch cleanup alone can take five minutes, in addition to bounded
        // Docker removals. Do not fail the executor while that cleanup is still valid.
        using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try
        {
            await lease.Work.WaitAsync(cleanupTimeout.Token);
            await CleanupAsync(lease).WaitAsync(cleanupTimeout.Token);
        }
        catch
        {
            executor.MarkUnavailable("Broker cleanup could not be confirmed.");
            throw new BrokerRequestException(503, "Build cleanup could not be confirmed.");
        }
        lock (_gate) _leases.Remove(id);
        lease.DisposeCancellation();
    }

    private async Task CleanupAsync(Lease lease)
    {
        await lease.ArtifactGate.WaitAsync();
        try
        {
            if (lease.CleanupFailed)
            {
                if (lease.Artifact is not null) await lease.Artifact.DisposeAsync();
                lease.Artifact = null;
                throw new InvalidOperationException("Previous cleanup failed.");
            }
            if (lease.Cleaned) return;
            try
            {
                if (lease.Artifact is not null) await lease.Artifact.DisposeAsync();
                lease.Artifact = null;
                if (lease.Prepared is not null) await lease.Prepared.DisposeAsync();
                lease.Cleaned = true;
            }
            catch
            {
                lease.CleanupFailed = true;
                executor.MarkUnavailable("Broker cleanup could not be confirmed.");
                throw;
            }
        }
        finally { lease.ArtifactGate.Release(); }
    }

    private Lease Find(string id)
    {
        RequireId(id);
        lock (_gate)
            return _leases.TryGetValue(id, out var lease) ? lease :
                throw new BrokerRequestException(404, "Build lease not found.");
    }

    private static void RequireId(string id)
    {
        if (!BuildPolicy.IsLowerHex(id, 32))
            throw new BrokerRequestException(404, "Build lease not found.");
    }

    public async Task ReapExpiredAsync()
    {
        Lease[] expired;
        lock (_gate) expired = _leases.Values.Where(l => l.ExpiresAt <= DateTimeOffset.UtcNow).ToArray();
        foreach (var lease in expired)
        {
            try { await ReleaseLeaseAsync(lease.Id); }
            catch (Exception error) { logger.LogError(error, "Expired broker lease cleanup was not confirmed"); }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken)) await ReapExpiredAsync();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Lease[] leases;
        lock (_gate) { _stopping = true; leases = _leases.Values.ToArray(); }
        foreach (var lease in leases) lease.Cancel();
        await Task.WhenAll(leases.Select(async lease =>
        {
            try { await ReleaseLeaseAsync(lease.Id); }
            catch (Exception error) { logger.LogError(error, "Broker shutdown cleanup was not confirmed"); }
        }));
        await base.StopAsync(cancellationToken);
    }

    private sealed class Lease : IOutputCapture
    {
        public readonly object Gate = new();
        public readonly string Id = Guid.NewGuid().ToString("N");
        public readonly DateTimeOffset ExpiresAt;
        public readonly CancellationTokenSource Cancellation;
        // Requests may retain a lease while internal cleanup disposes its source.
        public readonly CancellationToken Token;
        public readonly CancellationToken ExecutorGeneration;
        public readonly SemaphoreSlim ArtifactGate = new(1, 1);
        public readonly List<string> Logs = [];
        private int _logBytes;
        public Task Work = Task.CompletedTask;
        public IPreparedBuild? Prepared;
        public FileStream? Artifact;
        public BrokerBuildResult? Result;
        public string State = "preparing";
        public string? Error;
        public bool Cleaned;
        public bool CleanupFailed;
        private bool _cancellationDisposed;

        public Lease(CancellationToken stopToken, TimeSpan lifetime)
        {
            if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromMinutes(45))
                throw new InvalidOperationException("Invalid broker lease lifetime.");
            ExpiresAt = DateTimeOffset.UtcNow + lifetime;
            ExecutorGeneration = stopToken;
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
            Token = Cancellation.Token;
            Cancellation.CancelAfter(lifetime);
        }

        public void Cancel()
        {
            lock (Gate)
                if (!_cancellationDisposed) Cancellation.Cancel();
        }

        public void DisposeCancellation()
        {
            lock (Gate)
            {
                if (_cancellationDisposed) return;
                _cancellationDisposed = true;
                Cancellation.Dispose();
            }
        }

        public void AddLine(string line)
        {
            // Called from process event handlers: never throw from this callback.
            if (line.Length > BuildBrokerProtocol.MaximumLogLineCharacters)
                line = line[..BuildBrokerProtocol.MaximumLogLineCharacters];
            var bytes = Encoding.UTF8.GetByteCount(line) + 1;
            lock (Gate)
            {
                if (Logs.Count >= BuildPolicy.MaxBuildLogLines ||
                    _logBytes + bytes > BuildPolicy.MaxBuildLogBytes) return;
                Logs.Add(line); _logBytes += bytes;
            }
        }
    }
}
