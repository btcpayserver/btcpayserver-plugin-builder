using PluginBuilder.Services;

using PluginBuilder.Builds.Services;

namespace PluginBuilder.HostedServices;

public sealed class BuildBrokerMonitor(RemoteBuildSandbox broker, BuildExecutorState executor,
    ILogger<BuildBrokerMonitor> logger) : BackgroundService
{
    private string? _lastInstance;
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
                // Retain identity through failed probes. Only a confirmed restart ends
                // the old generation, even if its replacement is not ready yet.
                if (_lastInstance != status.InstanceId)
                    executor.MarkUnavailable("The isolated build broker instance changed.");
                _lastInstance = status.InstanceId;
                if (!status.IsReady)
                {
                    executor.SuspendAdmission("The isolated build broker is not ready.");
                    return;
                }
                if (executor.StopToken.IsCancellationRequested)
                    executor.MarkReady(status.WorkerImageId!, status.ProxyImageId!);
                else
                    executor.ResumeAdmission(status.WorkerImageId!, status.ProxyImageId!);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            executor.MarkUnavailable("The isolated build broker monitor was stopped.");
            throw;
        }
        catch (Exception error)
        {
            lock (_stateGate)
            {
                if (_stopping)
                    return;
                if (executor.Snapshot.IsReady)
                    logger.LogWarning("Build broker admission suspended ({ErrorType})", error.GetType().Name);
                executor.SuspendAdmission("The isolated build broker is unavailable.");
            }
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
