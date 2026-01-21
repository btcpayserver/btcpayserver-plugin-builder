using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using PluginBuilder.HostedServices;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.PluginTests;

public class PluginCleanupTests : UnitTestBase
{
    public PluginCleanupTests(ITestOutputHelper logs) : base(logs)
    {
    }

    [Fact]
    public async Task CleanupDeletesStalePluginsWithoutVersions()
    {
        // Arrange
        await using var tester = Create();
        tester.ReuseDatabase = false;
        await tester.Start();

        await using var conn = await tester.GetService<DBConnectionFactory>().Open();

        var userId = await tester.CreateFakeUserAsync();

        const string zombieSlug = "zombie-slug";
        const string freshSlug = "fresh-slug";
        const string veteranSlug = "veteran-slug";

        await conn.NewPlugin(zombieSlug, userId);
        await conn.NewPlugin(freshSlug, userId);
        await conn.NewPlugin(veteranSlug, userId);

        // Add a build to veteran-slug so it has a version
        var buildId = await conn.NewBuild(veteranSlug, new PluginBuildParameters(ServerTester.RepoUrl)
        {
            GitRef = ServerTester.GitRef,
            PluginDirectory = ServerTester.PluginDir
        });
        await conn.SetVersionBuild(new FullBuildId(veteranSlug, buildId), PluginVersion.Parse("1.0.0.0"), null, false);

        // Backdate zombie-slug and veteran-slug to 8 months ago
        var staleDate = DateTimeOffset.UtcNow.AddMonths(-8);
        await conn.ExecuteAsync(
            "UPDATE plugins SET added_at = @StaleDate WHERE slug IN (@Zombie, @Veteran)",
            new { StaleDate = staleDate, Zombie = zombieSlug, Veteran = veteranSlug });

        // Set fresh-slug to 10 days ago (within threshold)
        var recentDate = DateTimeOffset.UtcNow.AddDays(-10);
        await conn.ExecuteAsync(
            "UPDATE plugins SET added_at = @RecentDate WHERE slug = @Fresh",
            new { RecentDate = recentDate, Fresh = freshSlug });

        // Act
        var service = new PluginCleanupHostedService(
            tester.GetService<DBConnectionFactory>(),
            NullLogger<PluginCleanupHostedService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await service.StartAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected - the service runs indefinitely until cancelled
        }

        // Assert
        var zombieExists = await conn.ExecuteScalarAsync<string?>(
            "SELECT slug FROM plugins WHERE slug = @Slug",
            new { Slug = zombieSlug });
        var freshExists = await conn.ExecuteScalarAsync<string?>(
            "SELECT slug FROM plugins WHERE slug = @Slug",
            new { Slug = freshSlug });
        var veteranExists = await conn.ExecuteScalarAsync<string?>(
            "SELECT slug FROM plugins WHERE slug = @Slug",
            new { Slug = veteranSlug });

        Assert.Null(zombieExists); // Stale plugin without versions should be deleted
        Assert.Equal(freshSlug, freshExists); // Recent plugin should remain
        Assert.Equal(veteranSlug, veteranExists); // Old plugin with versions should remain
    }
}

