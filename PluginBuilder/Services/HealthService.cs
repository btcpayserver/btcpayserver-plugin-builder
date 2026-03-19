using Microsoft.Extensions.Diagnostics.HealthChecks;
using static PluginBuilder.HostedServices.AzureStartupHostedService;
using static PluginBuilder.HostedServices.DatabaseStartupHostedService;
using static PluginBuilder.HostedServices.DockerStartupException;

namespace PluginBuilder.Services;

public class HealthService : IHealthCheck
{
    Task<HealthCheckResult> IHealthCheck.CheckHealthAsync(HealthCheckContext healthCheckContext, CancellationToken cancellationToken)
    {
        if (!DatabaseStartupCompleted || !DockerStartupCompleted || !AzureStartupCompleted)
            return Task.FromResult(HealthCheckResult.Unhealthy("Startup incomplete"));

        if (DatabaseStartupError is not null)
            return Task.FromResult(HealthCheckResult.Unhealthy($"Database error: {DatabaseStartupError}"));

        if (DockerStartupError is not null)
            return Task.FromResult(HealthCheckResult.Unhealthy("Docker error: {DockerStartupError}"));

        if (AzureStartupError is not null)
            return Task.FromResult(HealthCheckResult.Unhealthy("Azure error: {AzureStartupError}"));

        return Task.FromResult(HealthCheckResult.Healthy());
    }
}
