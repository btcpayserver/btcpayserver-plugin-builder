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
    private CancellationToken? _suspendedGeneration;
    private int _consecutiveTimeouts;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Only probe timeouts are recoverable; cleanup and other hard failures
        // still require startup reconciliation.
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
        if (!await _probeGate.WaitAsync(0, stoppingToken))
            return;
        try
        {
            var generation = executor.StopToken;
            if (generation.IsCancellationRequested ||
                (!executor.Snapshot.IsReady && _suspendedGeneration != generation))
                return;
            try
            {
                // Fixed command, no shell, no mutable caller arguments and no output
                // accumulation: only exit status matters. ProcessRunner kills the
                // process tree when this deadline expires.
                var code = await DockerCli.RunAsync(processRunner, ["version", "--format", "{{.Server.Version}}"],
                    options.DockerProbeTimeout, stoppingToken, DiscardOutput.Instance, DiscardOutput.Instance);
                stoppingToken.ThrowIfCancellationRequested();
                if (code != 0)
                    FailClosed(generation, "Docker liveness probe returned a nonzero exit code.");
                else
                {
                    _consecutiveTimeouts = 0;
                    if (_suspendedGeneration == generation)
                    {
                        if (executor.TryResumeAdmission(generation))
                            logger.LogInformation("Docker responded again; build admission resumed.");
                        _suspendedGeneration = null;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal hosted shutdown is not evidence of a Docker failure.
                throw;
            }
            catch (OperationCanceledException)
            {
                _consecutiveTimeouts = Math.Min(_consecutiveTimeouts + 1, ConsecutiveTimeoutLimit);
                if (_consecutiveTimeouts >= ConsecutiveTimeoutLimit)
                {
                    if (executor.TrySuspendAdmission(generation, "Docker liveness probes timed out."))
                    {
                        _suspendedGeneration = generation;
                        logger.LogWarning("Docker probes timed out three times; new builds are suspended while accepted builds continue.");
                    }
                }
                else
                    logger.LogWarning("Docker liveness probe timed out ({TimeoutCount}/{TimeoutLimit}); retrying at the next probe.",
                        _consecutiveTimeouts, ConsecutiveTimeoutLimit);
            }
            catch (Exception error)
            {
                logger.LogWarning("Docker liveness probe failed ({ErrorType})", error.GetType().Name);
                FailClosed(generation, "Docker liveness probe failed.");
            }
        }
        finally { _probeGate.Release(); }
    }

    private void FailClosed(CancellationToken generation, string reason)
    {
        if (generation.IsCancellationRequested)
            return;
        logger.LogError("{Reason} New builds remain disabled until broker startup reconciliation.", reason);
        executor.MarkUnavailable(reason);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        // End the generation before waiting for probes: late successes cannot resume it.
        executor.MarkUnavailable("Build executor shutdown is in progress");
        return base.StopAsync(cancellationToken);
    }
}
