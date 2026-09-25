namespace PluginBuilder.Builds.Services;

public sealed record BuildExecutorSnapshot(
    bool IsReady,
    string? WorkerImageId,
    string? ProxyImageId,
    string? UnavailableReason);

public sealed class BuildExecutorState
{
    private readonly object _gate = new();
    private BuildExecutorSnapshot _snapshot = Unavailable("Build executor startup has not completed");
    private CancellationTokenSource _stopSource = CreateCancelledSource();

    public BuildExecutorSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public CancellationToken StopToken
    {
        get
        {
            lock (_gate)
                return _stopSource.Token;
        }
    }

    public void MarkReady(string workerImageId, string proxyImageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerImageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(proxyImageId);

        lock (_gate)
        {
            _stopSource.Cancel();
            _stopSource = new CancellationTokenSource();
            Interlocked.Exchange(ref _snapshot,
                new BuildExecutorSnapshot(true, workerImageId, proxyImageId, null));
        }
    }

    public void MarkUnavailable(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_gate)
        {
            Interlocked.Exchange(ref _snapshot, Unavailable(reason));
            _stopSource.Cancel();
        }
    }

    // Health probes control admission, not the lifetime of accepted jobs.
    public void SuspendAdmission(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_gate)
            Interlocked.Exchange(ref _snapshot, _snapshot with { IsReady = false, UnavailableReason = reason });
    }

    public bool TrySuspendAdmission(CancellationToken expectedGeneration, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_gate)
        {
            if (_stopSource.IsCancellationRequested || _stopSource.Token != expectedGeneration || !_snapshot.IsReady)
                return false;
            Interlocked.Exchange(ref _snapshot, _snapshot with { IsReady = false, UnavailableReason = reason });
            return true;
        }
    }

    public bool TryResumeAdmission(CancellationToken expectedGeneration)
    {
        lock (_gate)
        {
            if (_stopSource.IsCancellationRequested || _stopSource.Token != expectedGeneration ||
                _snapshot.WorkerImageId is null || _snapshot.ProxyImageId is null)
                return false;
            Interlocked.Exchange(ref _snapshot, _snapshot with { IsReady = true, UnavailableReason = null });
            return true;
        }
    }

    public void ResumeAdmission(string workerImageId, string proxyImageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerImageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(proxyImageId);
        lock (_gate)
        {
            if (_stopSource.IsCancellationRequested)
                throw new InvalidOperationException("Cannot resume admission for a stopped executor generation.");
            Interlocked.Exchange(ref _snapshot,
                new BuildExecutorSnapshot(true, workerImageId, proxyImageId, null));
        }
    }

    private static BuildExecutorSnapshot Unavailable(string reason)
    {
        return new BuildExecutorSnapshot(false, null, null, reason);
    }

    private static CancellationTokenSource CreateCancelledSource()
    {
        var source = new CancellationTokenSource();
        source.Cancel();
        return source;
    }
}
