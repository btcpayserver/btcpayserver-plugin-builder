using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using static PluginBuilder.HostedServices.AzureStartupHostedService;
using static PluginBuilder.HostedServices.DatabaseStartupHostedService;
using static PluginBuilder.HostedServices.DockerStartupException;

namespace PluginBuilder.Services;

public class HealthService : IHealthCheck
{
    public HealthService(DBConnectionFactory dbConnectionFactory, AzureStorageClient azureStorageClient, ProcessRunner processRunner)
    {
        DbConnectionFactory = dbConnectionFactory;
        AzureStorageClient = azureStorageClient;
        ProcessRunner = processRunner;
    }

    private DBConnectionFactory DbConnectionFactory { get; }
    private AzureStorageClient AzureStorageClient { get; }
    private ProcessRunner ProcessRunner { get; }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var startupCompleted = DatabaseStartupCompleted && DockerStartupCompleted && AzureStartupCompleted;
        if (!startupCompleted)
            return HealthCheckResult.Unhealthy("Startup incomplete");

        var hasStartupError = DatabaseStartupError is not null || DockerStartupError is not null || AzureStartupError is not null;
        if (hasStartupError)
            return HealthCheckResult.Unhealthy("Startup dependency failed");

        var dbTask = IsDatabaseHealthy(cancellationToken);
        var dockerTask = IsDockerHealthy(cancellationToken);
        var azureTask = AzureStorageClient.IsDefaultContainerAccessible();

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
