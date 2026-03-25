using System;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.PluginTests;

public class UserCleanupTests : UnitTestBase
{
    public UserCleanupTests(ITestOutputHelper logs) : base(logs)
    {
    }

    [Fact]
    public async Task CleanupDeletesOnlyStaleUnconfirmedUsersWithoutLinks()
    {
        await using var tester = Create();
        tester.ReuseDatabase = false;
        await tester.Start();

        await using var conn = await tester.GetService<DBConnectionFactory>().Open();

        // Fixture covers all keep/delete edge cases for unconfirmed accounts.
        var staleUnconfirmedDelete = await tester.CreateFakeUserAsync(confirmEmail: false, githubVerified: false);
        var recentUnconfirmedKeep = await tester.CreateFakeUserAsync(confirmEmail: false, githubVerified: false);
        var staleConfirmedKeep = await tester.CreateFakeUserAsync(confirmEmail: true, githubVerified: false);
        var staleWithRoleKeep = await tester.CreateFakeUserAsync(confirmEmail: false, githubVerified: false);
        var staleOwnerKeep = await tester.CreateFakeUserAsync(confirmEmail: false, githubVerified: false);
        var staleReviewerKeep = await tester.CreateFakeUserAsync(confirmEmail: false, githubVerified: false);
        var staleListingReviewerKeep = await tester.CreateFakeUserAsync(confirmEmail: false, githubVerified: false);

        var staleDate = DateTimeOffset.UtcNow.AddDays(-60);
        var recentDate = DateTimeOffset.UtcNow.AddDays(-5);

        // Mark most accounts stale so only recency saves the fresh unconfirmed one.
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
        var listingReviewerExists = await UserExists(conn, staleListingReviewerKeep);

        Assert.Equal(1, deletedCount);
        Assert.False(staleDeletedExists);
        Assert.True(recentExists);
        Assert.True(confirmedExists);
        Assert.True(roleExists);
        Assert.True(ownerExists);
        Assert.True(reviewerExists);
        Assert.True(listingReviewerExists);
    }

    private static async Task<bool> UserExists(System.Data.IDbConnection conn, string userId)
    {
        var exists = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM \"AspNetUsers\" WHERE \"Id\" = @UserId",
            new { UserId = userId });
        return exists > 0;
    }
}
