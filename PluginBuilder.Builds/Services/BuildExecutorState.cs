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
