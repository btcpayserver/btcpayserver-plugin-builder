using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.DataModels;
using PluginBuilder.HostedServices;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

using PluginBuilder.Builds.Services;

namespace PluginBuilder.Tests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class HealthServiceTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    [Fact]
    public async Task BuildExecutorHealthIsRequiredOnlyWhenNewBuildsAreEnabled()
    {
        await using var tester = Create("HealthBuildExecutorMatrix");
        tester.ReuseDatabase = false;
        tester.ConfigureServices = services =>
        {
            // This test controls the observed broker state itself. Never query
            // a real broker or let a background poll overwrite the matrix.
            foreach (var descriptor in services.Where(service => service.ServiceType == typeof(IHostedService) &&
                         service.ImplementationType == typeof(BuildBrokerMonitor)).ToArray())
                services.Remove(descriptor);
        };
        await tester.Start();

        var health = tester.GetService<HealthService>();
        var settings = tester.GetService<AdminSettingsCache>();
        var executor = tester.GetService<BuildExecutorState>();
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();

        var result = await health.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("Build executor unavailable", result.Description);

        await conn.SettingsSetAsync(SettingsKeys.NewBuildsEnabled, "false");
        await settings.RefreshFeatureSettings(conn);
        result = await health.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy, result.Status);

        await conn.SettingsSetAsync(SettingsKeys.NewBuildsEnabled, "true");
        await settings.RefreshFeatureSettings(conn);
        result = await health.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("Build executor unavailable", result.Description);

        executor.MarkReady(
            "sha256:1111111111111111111111111111111111111111111111111111111111111111",
            "sha256:2222222222222222222222222222222222222222222222222222222222222222");
        result = await health.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy, result.Status);

        executor.MarkUnavailable("The monitored broker became unavailable.");
        result = await health.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("Build executor unavailable", result.Description);
    }
}
