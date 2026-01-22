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
        await conn.UpdateAddedAtAsync(PluginSlug.Parse(zombieSlug), staleDate);
        await conn.UpdateAddedAtAsync(PluginSlug.Parse(veteranSlug), staleDate);

        // Set fresh-slug to 10 days ago (within threshold)
        var recentDate = DateTimeOffset.UtcNow.AddDays(-10);
        await conn.UpdateAddedAtAsync(PluginSlug.Parse(freshSlug), recentDate);

        // Act
        var service = new PluginCleanupHostedService(
            tester.GetService<DBConnectionFactory>(),
            NullLogger<PluginCleanupHostedService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await service.StartAsync(cts.Token);

        // Poll until zombie plugin is deleted or timeout
        string? zombieExists;
        do
        {
            await Task.Delay(100, cts.Token);
            zombieExists = await conn.ExecuteScalarAsync<string?>(
                "SELECT slug FROM plugins WHERE slug = @Slug",
                new { Slug = zombieSlug });
        } while (zombieExists is not null && !cts.Token.IsCancellationRequested);

        await service.StopAsync(CancellationToken.None);

        // Assert
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

