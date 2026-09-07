using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using PluginBuilder.Controllers.Logic;

namespace PluginBuilder.Services;

public class HealthService : IHealthCheck
{
    public HealthService(
        DBConnectionFactory dbConnectionFactory,
        AzureStorageClient azureStorageClient,
        ProcessRunner processRunner,
        IHostApplicationLifetime lifetime,
        AdminSettingsCache adminSettingsCache,
        BuildExecutorState executorState)
    {
        DbConnectionFactory = dbConnectionFactory;
        AzureStorageClient = azureStorageClient;
        ProcessRunner = processRunner;
        AdminSettingsCache = adminSettingsCache;
        ExecutorState = executorState;
        _lifetime = lifetime;
    }

    private readonly IHostApplicationLifetime _lifetime;

    private DBConnectionFactory DbConnectionFactory { get; }
    private AzureStorageClient AzureStorageClient { get; }
    private ProcessRunner ProcessRunner { get; }
    private AdminSettingsCache AdminSettingsCache { get; }
    private BuildExecutorState ExecutorState { get; }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!_lifetime.ApplicationStarted.IsCancellationRequested)
            return HealthCheckResult.Unhealthy("Startup incomplete");

        var dbTask = IsDatabaseHealthy(cancellationToken);
        var azureTask = AzureStorageClient.IsDefaultContainerAccessible(cancellationToken);

        await Task.WhenAll(dbTask, azureTask);

        if (!dbTask.Result || !azureTask.Result)
            return HealthCheckResult.Unhealthy("Critical dependency unavailable");

        if (!AdminSettingsCache.NewBuildsEnabled)
            return HealthCheckResult.Healthy();

        var executor = ExecutorState.Snapshot;
        if (!executor.IsReady)
            return HealthCheckResult.Unhealthy("Build executor unavailable");

        var dockerHealthy = await IsDockerHealthy(cancellationToken);

        return dockerHealthy
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Build executor Docker daemon unavailable");
    }

    private async Task<bool> IsDatabaseHealthy(CancellationToken cancellationToken)
    {
        try
        {
            await using var conn = await DbConnectionFactory.Open(cancellationToken);
            await using var cmd = new NpgsqlCommand("SELECT 1", conn);
            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            return result is 1;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> IsDockerHealthy(CancellationToken cancellationToken)
    {
        try
        {
            var code = await ProcessRunner.RunAsync(new ProcessSpec
            {
                Executable = "docker",
                Arguments = ["info", "--format", "{{ .ServerVersion }}"],
                OutputCapture = new OutputCapture(),
                ErrorCapture = new OutputCapture()
            }, cancellationToken);
            return code == 0;
        }
        catch
        {
            return false;
        }
    }
}
