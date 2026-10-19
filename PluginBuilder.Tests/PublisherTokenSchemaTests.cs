using Dapper;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests;

public class PublisherTokenSchemaTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    [Fact]
    public async Task BothTokenScopesKeepForeignKeysAndOnlyAllPluginTokensSurvivePluginDeletion()
    {
        await using var tester = CreateMigrationTester("PublisherTokenSchema");
        await tester.RunScriptsUntil("29.PublisherAccessTokens");
        await using var conn = await tester.Open();
        var scopedId = Guid.NewGuid();
        var allId = Guid.NewGuid();
        await conn.ExecuteAsync("""
            INSERT INTO "AspNetUsers"
                ("Id", "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
            VALUES ('publisher', TRUE, FALSE, FALSE, FALSE, 0);
            INSERT INTO plugins (slug) VALUES ('scope-migration');
            """);

        await tester.RunRemainingScripts();

        await conn.ExecuteAsync("""
            INSERT INTO publisher_access_tokens (id, user_id, plugin_slug, name, token_hash, security_stamp)
            VALUES (@scopedId, 'publisher', 'scope-migration', 'Plugin CI', 'original-hash', 'original-stamp');
            """, new { scopedId });

        var existing = await conn.QuerySingleAsync<(string Plugin, string Hash, string Stamp)>("""
            SELECT plugin_slug AS Plugin, token_hash AS Hash, security_stamp AS Stamp
            FROM publisher_access_tokens WHERE id = @scopedId
            """, new { scopedId });
        Assert.Equal(("scope-migration", "original-hash", "original-stamp"), existing);
        await conn.ExecuteAsync("""
            INSERT INTO publisher_access_tokens (id, user_id, plugin_slug, name, token_hash)
            VALUES (@allId, 'publisher', NULL, 'All plugins CI', 'all-plugins-hash')
            """, new { allId });
        var missingPlugin = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync("""
            INSERT INTO publisher_access_tokens (id, user_id, plugin_slug, name, token_hash)
            VALUES (@id, 'publisher', 'nonexistent', 'Invalid CI', 'invalid-hash')
            """, new { id = Guid.NewGuid() }));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, missingPlugin.SqlState);
        await conn.ExecuteAsync("DELETE FROM plugins WHERE slug = 'scope-migration'");
        Assert.Equal(allId, await conn.QuerySingleAsync<Guid>("SELECT id FROM publisher_access_tokens"));
        await conn.ExecuteAsync("DELETE FROM \"AspNetUsers\" WHERE \"Id\" = 'publisher'");
        Assert.Empty(await conn.QueryAsync<Guid>("SELECT id FROM publisher_access_tokens"));
    }
}
