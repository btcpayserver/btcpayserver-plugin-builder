using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace PluginBuilder.Services;

public class HealthService : IHealthCheck
{
    public HealthService(DBConnectionFactory dbConnectionFactory, AzureStorageClient azureStorageClient, ProcessRunner processRunner, IHostApplicationLifetime lifetime)
    {
        DbConnectionFactory = dbConnectionFactory;
        AzureStorageClient = azureStorageClient;
        ProcessRunner = processRunner;

        _lifetime = lifetime;
    }

    private readonly IHostApplicationLifetime _lifetime;

    private DBConnectionFactory DbConnectionFactory { get; }
    private AzureStorageClient AzureStorageClient { get; }
    private ProcessRunner ProcessRunner { get; }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!_lifetime.ApplicationStarted.IsCancellationRequested)
            return HealthCheckResult.Unhealthy("Startup incomplete");

        var dbTask = IsDatabaseHealthy(cancellationToken);
        var dockerTask = IsDockerHealthy(cancellationToken);
        var azureTask = AzureStorageClient.IsDefaultContainerAccessible(cancellationToken);

        await Task.WhenAll(dbTask, dockerTask, azureTask);

        var isHealthy = dbTask.Result && dockerTask.Result && azureTask.Result;

        return isHealthy
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Critical dependency unavailable");
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
