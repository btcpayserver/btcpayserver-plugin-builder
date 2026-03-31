using System.Threading.Tasks;
using Dapper;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests;

public class DatabaseMigrationTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    [Fact]
    public async Task CanDropLegacyPluginReviewsUserId()
    {
        await using var tester = CreateMigrationTester();
        await tester.RunScriptsUntil("21.DropLegacyPluginReviewUserFk");

        await using (var conn = await tester.Open())
        {
            const string userId = "review-user";
            const string pluginSlug = "review-migration-plugin";

            await conn.ExecuteAsync(
                """
                INSERT INTO "AspNetUsers"
                ("Id", "UserName", "NormalizedUserName", "Email", "NormalizedEmail", "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
                VALUES
                (@UserId, 'review-user', 'REVIEW-USER', 'review@example.com', 'REVIEW@EXAMPLE.COM', TRUE, FALSE, FALSE, FALSE, 0);

                INSERT INTO plugins (slug) VALUES (@PluginSlug);

                INSERT INTO plugin_reviewers (user_id, source)
                VALUES (@UserId, 'system');

                INSERT INTO plugin_reviews (plugin_slug, user_id, reviewer_id, rating, body)
                SELECT @PluginSlug, @UserId, id, 5, 'Great plugin'
                FROM plugin_reviewers
                WHERE user_id = @UserId;
                """,
                new { UserId = userId, PluginSlug = pluginSlug });

            Assert.True(await conn.ExecuteScalarAsync<bool>(
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_name = 'plugin_reviews'
                      AND column_name = 'user_id'
                )
                """));
        }

        await tester.RunRemainingScripts();

        await using var migratedConn = await tester.Open();

        var hasUserIdColumn = await migratedConn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_name = 'plugin_reviews'
                  AND column_name = 'user_id'
            )
            """);
        var hasLegacyUserIdConstraint = await migratedConn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.table_constraints
                WHERE table_name = 'plugin_reviews'
                  AND constraint_name = 'fk_plugin_reviews_user'
            )
            """);
        var migratedReview = await migratedConn.QuerySingleAsync<(string PluginSlug, long ReviewerId, string ReviewerUserId)>(
            """
            SELECT r.plugin_slug AS PluginSlug, r.reviewer_id AS ReviewerId, pr.user_id AS ReviewerUserId
            FROM plugin_reviews r
            JOIN plugin_reviewers pr ON pr.id = r.reviewer_id
            WHERE r.plugin_slug = @PluginSlug
            """,
            new { PluginSlug = "review-migration-plugin" });

        Assert.False(hasUserIdColumn);
        Assert.False(hasLegacyUserIdConstraint);
        Assert.Equal("review-migration-plugin", migratedReview.PluginSlug);
        Assert.True(migratedReview.ReviewerId > 0);
        Assert.Equal("review-user", migratedReview.ReviewerUserId);
    }
}
