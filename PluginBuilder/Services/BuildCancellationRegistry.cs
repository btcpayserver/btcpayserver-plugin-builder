using System.Collections.Concurrent;

namespace PluginBuilder.Services;

/// <summary>
/// Lets an admin stop a build this instance is running or holding for an execution slot. The database state is
/// authoritative (the pipeline's updates skip a finished build); this only makes the pipeline stop waiting sooner.
/// </summary>
public sealed class BuildCancellationRegistry
{
    private readonly ConcurrentDictionary<FullBuildId, CancellationTokenSource> _running = new();

    public CancellationTokenSource Register(FullBuildId buildId, CancellationToken stopping)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        _running[buildId] = source;
        return source;
    }

    public void Unregister(FullBuildId buildId, CancellationTokenSource source)
    {
        _running.TryRemove(new KeyValuePair<FullBuildId, CancellationTokenSource>(buildId, source));
        source.Dispose();
    }

    /// <returns>Whether this instance was running the build.</returns>
    public bool Cancel(FullBuildId buildId)
    {
        if (!_running.TryGetValue(buildId, out var source))
            return false;
        try { source.Cancel(); }
        catch (ObjectDisposedException) { return false; }
        return true;
    }
}
