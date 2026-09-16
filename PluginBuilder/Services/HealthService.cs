using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using PluginBuilder.Controllers.Logic;

using PluginBuilder.Builds.Services;

namespace PluginBuilder.Services;

public class HealthService : IHealthCheck
{
    public HealthService(
        DBConnectionFactory dbConnectionFactory,
        AzureStorageClient azureStorageClient,
        IHostApplicationLifetime lifetime,
        AdminSettingsCache adminSettingsCache,
        BuildExecutorState executorState)
    {
        DbConnectionFactory = dbConnectionFactory;
        AzureStorageClient = azureStorageClient;
        AdminSettingsCache = adminSettingsCache;
        ExecutorState = executorState;
        _lifetime = lifetime;
    }

    private readonly IHostApplicationLifetime _lifetime;

    private DBConnectionFactory DbConnectionFactory { get; }
    private AzureStorageClient AzureStorageClient { get; }
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

        // Docker lives exclusively in the internal broker. Its monitored readiness
        // is the public application's executor dependency; no local CLI/socket probe.
        return HealthCheckResult.Healthy();
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

}
