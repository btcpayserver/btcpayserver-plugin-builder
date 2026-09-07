using Microsoft.Extensions.Diagnostics.HealthChecks;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.DataModels;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class HealthServiceTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    [Fact]
    public async Task BuildExecutorHealthIsRequiredOnlyWhenNewBuildsAreEnabled()
    {
        if (OperatingSystem.IsWindows())
            return;

        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            $"plugin-builder-health-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        var dockerPath = Path.Combine(tempDirectory, "docker");
        await File.WriteAllTextAsync(dockerPath, """
            #!/bin/sh
            set -eu
            state="${PB_HEALTH_TEST_STATE:?}"
            printf '%s\n' "$*" >> "$state/commands"
            [ -f "$state/docker-healthy" ]
            """);
        File.SetUnixFileMode(
            dockerPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalSkipBuild = Environment.GetEnvironmentVariable("DOCKER_STARTUP_SKIP_BUILD");
        var originalFakeState = Environment.GetEnvironmentVariable("PB_HEALTH_TEST_STATE");
        try
        {
            Environment.SetEnvironmentVariable("DOCKER_STARTUP_SKIP_BUILD", "true");

            await using var tester = Create("HealthBuildExecutorMatrix");
            tester.ReuseDatabase = false;
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

            Environment.SetEnvironmentVariable(
                "PATH",
                tempDirectory + Path.PathSeparator + originalPath);
            Environment.SetEnvironmentVariable("PB_HEALTH_TEST_STATE", tempDirectory);

            result = await health.CheckHealthAsync(new HealthCheckContext());
            Assert.Equal(HealthStatus.Healthy, result.Status);
            Assert.False(File.Exists(Path.Combine(tempDirectory, "commands")));

            await conn.SettingsSetAsync(SettingsKeys.NewBuildsEnabled, "true");
            await settings.RefreshFeatureSettings(conn);

            result = await health.CheckHealthAsync(new HealthCheckContext());
            Assert.Equal(HealthStatus.Unhealthy, result.Status);
            Assert.Equal("Build executor unavailable", result.Description);
            Assert.False(File.Exists(Path.Combine(tempDirectory, "commands")));

            executor.MarkReady(
                "sha256:1111111111111111111111111111111111111111111111111111111111111111",
                "sha256:2222222222222222222222222222222222222222222222222222222222222222");

            result = await health.CheckHealthAsync(new HealthCheckContext());
            Assert.Equal(HealthStatus.Unhealthy, result.Status);
            Assert.Equal("Build executor Docker daemon unavailable", result.Description);

            await File.WriteAllTextAsync(
                Path.Combine(tempDirectory, "docker-healthy"),
                string.Empty);
            result = await health.CheckHealthAsync(new HealthCheckContext());
            Assert.Equal(HealthStatus.Healthy, result.Status);

            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable("PB_HEALTH_TEST_STATE", originalFakeState);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable("DOCKER_STARTUP_SKIP_BUILD", originalSkipBuild);
            Environment.SetEnvironmentVariable("PB_HEALTH_TEST_STATE", originalFakeState);
            Directory.Delete(tempDirectory, recursive: true);
        }
    }
}
