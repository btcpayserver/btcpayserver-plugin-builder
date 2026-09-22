using PluginBuilder.BuildBroker.Configuration;
using PluginBuilder.BuildBroker.Services;
using PluginBuilder.Builds.Services;

namespace PluginBuilder.BuildBroker;

/// <summary>
/// Docker liveness belongs to the socket-owning broker, never the public web app.
/// Probes are periodic and serialized, not triggered by incoming health requests.
/// </summary>
public sealed class BrokerDockerMonitor(
    ProcessRunner processRunner,
    BuildExecutorState executor,
    BuildExecutorOptions options,
    ILogger<BrokerDockerMonitor> logger) : BackgroundService
{
    public static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(15);
    private const int ConsecutiveTimeoutLimit = 3;
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private int _consecutiveTimeouts;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Startup already verified Docker and the pinned isolation profile. A later
        // confirmed liveness failure requires startup reconciliation, not automatic readmission.
        using var timer = new PeriodicTimer(ProbeInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await CheckOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public async Task CheckOnceAsync(CancellationToken stoppingToken = default)
    {
        stoppingToken.ThrowIfCancellationRequested();
        if (!executor.Snapshot.IsReady || !await _probeGate.WaitAsync(0, stoppingToken))
            return;
        try
        {
            if (!executor.Snapshot.IsReady)
                return;
            try
            {
                // Fixed command, no shell, no mutable caller arguments and no output
                // accumulation: only exit status matters. ProcessRunner kills the
                // process tree when this deadline expires.
                var code = await DockerCli.RunAsync(processRunner, ["version", "--format", "{{.Server.Version}}"],
                    options.DockerProbeTimeout, stoppingToken, DiscardOutput.Instance, DiscardOutput.Instance);
                if (code != 0)
                    FailClosed("Docker liveness probe returned a nonzero exit code.");
                else
                    _consecutiveTimeouts = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal hosted shutdown is not evidence of a Docker failure.
                throw;
            }
            catch (OperationCanceledException)
            {
                _consecutiveTimeouts++;
                if (_consecutiveTimeouts >= ConsecutiveTimeoutLimit)
                    FailClosed("Docker liveness probe timed out on three consecutive attempts.");
                else
                    logger.LogWarning("Docker liveness probe timed out ({TimeoutCount}/{TimeoutLimit}); retrying at the next probe.",
                        _consecutiveTimeouts, ConsecutiveTimeoutLimit);
            }
            catch (Exception error)
            {
                logger.LogWarning("Docker liveness probe failed ({ErrorType})", error.GetType().Name);
                FailClosed("Docker liveness probe failed.");
            }
        }
        finally { _probeGate.Release(); }
    }

    private void FailClosed(string reason)
    {
        if (!executor.Snapshot.IsReady)
            return;
        logger.LogError("{Reason} New builds remain disabled until broker startup reconciliation.", reason);
        executor.MarkUnavailable(reason);
    }
}
