using PluginBuilder.Services;

using PluginBuilder.Builds.BuildBroker;
using PluginBuilder.Builds.Services;

namespace PluginBuilder.HostedServices;

public sealed class BuildBrokerMonitor(RemoteBuildSandbox broker, BuildExecutorState executor,
    ILogger<BuildBrokerMonitor> logger) : BackgroundService
{
    private BrokerStatus? _lastReady;
    private readonly object _stateGate = new();
    private bool _stopping;

    public async Task CheckOnceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await broker.GetStatusAsync(cancellationToken);
            lock (_stateGate)
            {
                if (_stopping)
                    return;
                if (!status.IsReady)
                {
                    _lastReady = null;
                    executor.MarkUnavailable("The isolated build broker is not ready.");
                    return;
                }
                if (_lastReady?.InstanceId != status.InstanceId || _lastReady.WorkerImageId != status.WorkerImageId ||
                    _lastReady.ProxyImageId != status.ProxyImageId || !executor.Snapshot.IsReady)
                {
                    // MarkReady cancels the old executor token. Invoke only when identity
                    // changed/recovered, never on every successful health poll.
                    executor.MarkReady(status.WorkerImageId!, status.ProxyImageId!);
                    _lastReady = status;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            executor.MarkUnavailable("The isolated build broker monitor was stopped.");
            throw;
        }
        catch (Exception error)
        {
            if (executor.Snapshot.IsReady)
                logger.LogWarning("Build broker became unavailable ({ErrorType})", error.GetType().Name);
            _lastReady = null;
            executor.MarkUnavailable("The isolated build broker is unavailable.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await CheckOnceAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            executor.MarkUnavailable("The isolated build broker monitor was stopped.");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_stateGate) _stopping = true;
        // Cancel the health poll and builds together, while the authenticated HTTP
        // transport is still alive. DI disposal occurs after hosted StopAsync.
        await Task.WhenAll(base.StopAsync(cancellationToken), broker.StopAsync(cancellationToken));
    }
}
