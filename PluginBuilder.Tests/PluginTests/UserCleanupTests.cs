using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using PluginBuilder.Controllers;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.DataModels;
using PluginBuilder.HostedServices;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using PluginBuilder.Util;
using PluginBuilder.ViewModels.Admin;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.PluginTests;

public class UserCleanupTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    [Fact]
    public async Task CleanupDeletesOnlyStaleUnconfirmedUsersWithoutLinks()
    {
        await using var tester = CreateCleanupTester();
        await tester.Start();

        await using var conn = await tester.GetService<DBConnectionFactory>().Open();

        var staleUnconfirmedDelete = await tester.CreateFakeUserAsync(confirmEmail: false, githubVerified: false);
        var recentUnconfirmedKeep = await tester.CreateFakeUserAsync(confirmEmail: false, githubVerified: false);
        var staleConfirmedKeep = await tester.CreateFakeUserAsync(confirmEmail: true, githubVerified: false);
        var staleWithRoleKeep = await tester.CreateFakeUserAsync(confirmEmail: false, githubVerified: false);
        var staleOwnerKeep = await tester.CreateFakeUserAsync(confirmEmail: false, githubVerified: false);
        var staleReviewerKeep = await tester.CreateFakeUserAsync(confirmEmail: false, githubVerified: false);
        var staleVoteOnlyKeep = await tester.CreateFakeUserAsync(confirmEmail: false, githubVerified: false);
        var staleListingReviewerKeep = await tester.CreateFakeUserAsync(confirmEmail: false, githubVerified: false);

        var staleDate = DateTimeOffset.UtcNow.AddDays(-60);
        var recentDate = DateTimeOffset.UtcNow.AddDays(-5);

        await conn.ExecuteAsync(
            "UPDATE \"AspNetUsers\" SET \"CreatedAt\" = @StaleDate WHERE \"Id\" = ANY(@StaleIds)",
            new
            {
                StaleDate = staleDate,
                StaleIds = new[]
                {
                    staleUnconfirmedDelete,
                    staleConfirmedKeep,
                    staleWithRoleKeep,
                    staleOwnerKeep,
                    staleReviewerKeep,
                    staleVoteOnlyKeep,
                    staleListingReviewerKeep
                }
            });

        await conn.ExecuteAsync(
            "UPDATE \"AspNetUsers\" SET \"CreatedAt\" = @RecentDate WHERE \"Id\" = @UserId",
            new { RecentDate = recentDate, UserId = recentUnconfirmedKeep });

        var serverAdminRoleId = await conn.QuerySingleAsync<string>(
            "SELECT \"Id\" FROM \"AspNetRoles\" WHERE \"NormalizedName\" = 'SERVERADMIN'");
        await conn.ExecuteAsync(
            "INSERT INTO \"AspNetUserRoles\" (\"UserId\", \"RoleId\") VALUES (@UserId, @RoleId)",
            new { UserId = staleWithRoleKeep, RoleId = serverAdminRoleId });

        const string ownerPlugin = "owner-linked-plugin";
        await conn.NewPlugin(ownerPlugin, staleOwnerKeep);

        const string reviewPlugin = "review-linked-plugin";
        await conn.NewPlugin(reviewPlugin, staleOwnerKeep);
        var reviewerId = await conn.QuerySingleAsync<long>(
            "INSERT INTO plugin_reviewers (user_id, source) VALUES (@UserId, 'system') RETURNING id",
            new { UserId = staleReviewerKeep });
        await conn.ExecuteAsync(
            "INSERT INTO plugin_reviews (plugin_slug, reviewer_id, rating, body) VALUES (@PluginSlug, @ReviewerId, 5, 'ok')",
            new { PluginSlug = reviewPlugin, ReviewerId = reviewerId });
        await conn.ExecuteAsync(
            "UPDATE plugin_reviews SET helpful_voters = jsonb_build_object(@UserId, true) WHERE plugin_slug = @PluginSlug AND reviewer_id = @ReviewerId",
            new { UserId = staleVoteOnlyKeep, PluginSlug = reviewPlugin, ReviewerId = reviewerId });

        const string listingPlugin = "listing-linked-plugin";
        await conn.NewPlugin(listingPlugin, staleOwnerKeep);
        await conn.ExecuteAsync(
            """
            INSERT INTO plugin_listing_requests
            (plugin_slug, release_note, telegram_verification_message, user_reviews, status, reviewed_at, reviewed_by)
            VALUES (@PluginSlug, 'r', 't', 'u', 'approved', CURRENT_TIMESTAMP, @ReviewedBy)
            """,
            new { PluginSlug = listingPlugin, ReviewedBy = staleListingReviewerKeep });

        using var scope = tester.WebApp.Services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<UserCleanupRunner>();
        var deletedCount = await runner.RunOnceAsync();

        var staleDeletedExists = await UserExists(conn, staleUnconfirmedDelete);
        var recentExists = await UserExists(conn, recentUnconfirmedKeep);
        var confirmedExists = await UserExists(conn, staleConfirmedKeep);
        var roleExists = await UserExists(conn, staleWithRoleKeep);
        var ownerExists = await UserExists(conn, staleOwnerKeep);
        var reviewerExists = await UserExists(conn, staleReviewerKeep);
        var voteOnlyExists = await UserExists(conn, staleVoteOnlyKeep);
        var listingReviewerExists = await UserExists(conn, staleListingReviewerKeep);

        Assert.Equal(1, deletedCount);
        Assert.False(staleDeletedExists);
        Assert.True(recentExists);
        Assert.True(confirmedExists);
        Assert.True(roleExists);
        Assert.True(ownerExists);
        Assert.True(reviewerExists);
        Assert.True(voteOnlyExists);
        Assert.True(listingReviewerExists);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupPreservesWhitelistedAccountUntilRevocation(bool requireConfirmedEmail)
    {
        await using var tester = CreateCleanupTester("WhitelistUserCleanup");
        await tester.Start();
        const string email = "kelvin-cleanup@example.test";
        var userId = await tester.CreateFakeUserAsync(email, confirmEmail: false, githubVerified: false);
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.ExecuteAsync(
            "UPDATE \"AspNetUsers\" SET \"CreatedAt\" = @CreatedAt WHERE \"Id\" = @userId",
            new { CreatedAt = DateTimeOffset.UtcNow.AddDays(-60), userId });
        await conn.SettingsSetAsync(SettingsKeys.NewBuildsEnabled, "false");
        await conn.SettingsSetAsync(SettingsKeys.VerifiedEmailForLogin, requireConfirmedEmail ? "true" : "false");
        await conn.ExecuteAsync("INSERT INTO build_whitelist (user_id) VALUES (@userId)", new { userId });
        await tester.GetService<AdminSettingsCache>().RefreshAllAdminSettings(conn);

        using var scope = tester.WebApp.Services.CreateScope();
        var access = scope.ServiceProvider.GetRequiredService<BuildAccessLogic>();
        var runner = scope.ServiceProvider.GetRequiredService<UserCleanupRunner>();
        var principal = Principal(userId);
        Assert.Equal(!requireConfirmedEmail, await access.CanCreateBuild(principal));

        Assert.Equal(0, await runner.RunOnceAsync());
        Assert.True(await UserExists(conn, userId));

        await conn.ExecuteAsync("DELETE FROM build_whitelist WHERE user_id = @userId", new { userId });
        Assert.False(await access.CanCreateBuild(principal));
        Assert.Equal(1, await runner.RunOnceAsync());
        Assert.False(await UserExists(conn, userId));

        // Reusing a revoked account's email must not inherit the earlier authorization.
        var replacementId = await tester.CreateFakeUserAsync(email, confirmEmail: false, githubVerified: false);
        Assert.NotEqual(userId, replacementId);
        Assert.False(await access.IsWhitelisted(Principal(replacementId)));
        Assert.False(await access.CanCreateBuild(Principal(replacementId)));
    }

    [Fact]
    public async Task CleanupWaitsForConcurrentWhitelistGrantBeforeDeletingUsers()
    {
        await using var tester = CreateCleanupTester("WhitelistCleanupGrant");
        await tester.Start();
        const string email = "concurrent-cleanup@example.test";
        var userId = await tester.CreateFakeUserAsync(email, confirmEmail: false, githubVerified: false);
        await using var granting = await tester.GetService<DBConnectionFactory>().Open();
        await using var observer = await tester.GetService<DBConnectionFactory>().Open();
        await granting.ExecuteAsync(
            "UPDATE \"AspNetUsers\" SET \"CreatedAt\" = @CreatedAt WHERE \"Id\" = @userId",
            new { CreatedAt = DateTimeOffset.UtcNow.AddDays(-60), userId });
        await using var grant = await granting.BeginTransactionAsync();
        await granting.ExecuteAsync("LOCK TABLE build_whitelist IN SHARE ROW EXCLUSIVE MODE");
        using var scope = tester.WebApp.Services.CreateScope();
        var pending = scope.ServiceProvider.GetRequiredService<UserCleanupRunner>().RunOnceAsync();
        try
        {
            await WaitForBlockedConnection(observer, granting.ProcessID, pending);
            await granting.ExecuteAsync("INSERT INTO build_whitelist (user_id) VALUES (@userId)", new { userId });
            await grant.CommitAsync();

            Assert.Equal(0, await pending.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(await UserExists(observer, userId));
            Assert.Equal(userId, await observer.QuerySingleAsync<string>("SELECT user_id FROM build_whitelist"));
        }
        finally
        {
            await grant.DisposeAsync();
            try
            {
                await pending.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch
            {
                // Preserve the original assertion failure while releasing the blocked connection.
            }
        }
    }

    [Fact]
    public async Task WhitelistGrantWaitsForCleanupAndRejectsTheDeletedAccount()
    {
        await using var tester = CreateCleanupTester("WhitelistGrantAfterCleanup");
        await tester.Start();
        var userId = await tester.CreateFakeUserAsync("cleanup-before-grant@example.test", confirmEmail: false, githubVerified: false);
        var adminId = await tester.CreateFakeUserAsync();
        await using var blocking = await tester.GetService<DBConnectionFactory>().Open();
        await using var observer = await tester.GetService<DBConnectionFactory>().Open();
        await blocking.ExecuteAsync(
            "UPDATE \"AspNetUsers\" SET \"CreatedAt\" = @CreatedAt WHERE \"Id\" = @userId",
            new { CreatedAt = DateTimeOffset.UtcNow.AddDays(-60), userId });
        await blocking.SettingsSetAsync(SettingsKeys.VerifiedEmailForLogin, "false");
        await tester.GetService<AdminSettingsCache>().RefreshAllAdminSettings(blocking);

        using var adminScope = tester.WebApp.Services.CreateScope();
        var admin = ActivatorUtilities.CreateInstance<AdminController>(adminScope.ServiceProvider);
        admin.ControllerContext = new ControllerContext
        {
            RouteData = new Microsoft.AspNetCore.Routing.RouteData(),
            ActionDescriptor = new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor(),
            HttpContext = new DefaultHttpContext
            {
                RequestServices = adminScope.ServiceProvider,
                User = new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim(ClaimTypes.NameIdentifier, adminId),
                    new Claim(ClaimTypes.Role, Roles.ServerAdmin)
                ], "test"))
            }
        };
        var editor = Assert.IsType<SettingsEditorViewModel>(Assert.IsType<ViewResult>(await admin.SettingsEditor()).Model);

        // Pause the real cleanup at DELETE, after it has acquired the whitelist table lock.
        await using var rowLock = await blocking.BeginTransactionAsync();
        await blocking.QuerySingleAsync<string>("SELECT \"Id\" FROM \"AspNetUsers\" WHERE \"Id\" = @userId FOR UPDATE", new { userId });
        using var cleanupScope = tester.WebApp.Services.CreateScope();
        var cleanup = cleanupScope.ServiceProvider.GetRequiredService<UserCleanupRunner>().RunOnceAsync();
        Task<IActionResult>? grant = null;
        try
        {
            var cleanupPid = await WaitForBlockedConnection(observer, blocking.ProcessID, cleanup);
            Assert.True(await observer.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM pg_locks WHERE pid = @cleanupPid AND relation = 'build_whitelist'::regclass AND mode = 'ShareRowExclusiveLock' AND granted)",
                new { cleanupPid }), "Cleanup must retain its whitelist lock while deleting accounts.");

            grant = admin.SettingsEditor(SettingsKeys.NewBuildsWhitelist, "cleanup-before-grant@example.test", editor.WhitelistVersion);
            await WaitForBlockedConnection(observer, cleanupPid, grant);
            await rowLock.CommitAsync();

            Assert.Equal(1, await cleanup.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.IsType<RedirectToActionResult>(await grant.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Contains("must identify one existing account", Assert.IsType<string>(admin.TempData[TempDataConstant.WarningMessage]), StringComparison.Ordinal);
            Assert.False(await UserExists(observer, userId));
            Assert.Empty(await observer.QueryAsync<string>("SELECT user_id FROM build_whitelist"));
        }
        finally
        {
            await rowLock.DisposeAsync();
            try
            {
                await cleanup.WaitAsync(TimeSpan.FromSeconds(10));
                if (grant is not null)
                    await grant.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch
            {
                // Preserve the original assertion failure while releasing blocked connections.
            }
        }
    }

    private ServerTester CreateCleanupTester(string name = "UserCleanup")
    {
        var tester = Create(name);
        tester.ReuseDatabase = false;
        tester.ConfigureServices = services =>
        {
            // These tests control each cleanup run and must not race the startup run.
            foreach (var descriptor in services.Where(descriptor =>
                         descriptor.ServiceType == typeof(IHostedService) &&
                         (descriptor.ImplementationType == typeof(UserCleanupHostedService) ||
                          descriptor.ImplementationType == typeof(DockerStartupHostedService) ||
                          descriptor.ImplementationType == typeof(AzureStartupHostedService))).ToArray())
                services.Remove(descriptor);
        };
        return tester;
    }

    private static async Task<int> WaitForBlockedConnection(NpgsqlConnection observer, int blockingPid, Task pending)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var waitingPid = await observer.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT pid FROM pg_stat_activity WHERE datname = current_database() AND @blockingPid = ANY(pg_blocking_pids(pid)) LIMIT 1",
                new { blockingPid }, cancellationToken: timeout.Token));
            if (waitingPid is not null)
                return waitingPid.Value;
            Assert.False(pending.IsCompleted, "The operation must wait for the competing transaction.");
            await Task.Delay(10, timeout.Token);
        }
    }

    private static ClaimsPrincipal Principal(string userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"));

    private static async Task<bool> UserExists(System.Data.IDbConnection conn, string userId)
    {
        var exists = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM \"AspNetUsers\" WHERE \"Id\" = @UserId",
            new { UserId = userId });
        return exists > 0;
    }
}
