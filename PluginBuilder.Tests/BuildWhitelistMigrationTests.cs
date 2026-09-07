using Dapper;
using Npgsql;
using PluginBuilder.DataModels;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests;

public class BuildWhitelistMigrationTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    [Fact]
    public async Task MigrationStartsEmptyWithoutGrantingExistingAccounts()
    {
        await using var tester = CreateMigrationTester("BuildWhitelistEmptyMigration");
        await tester.RunScriptsUntil("25.BuildWhitelist");
        await using var conn = await tester.Open();
        await InsertUser(conn, "confirmed", confirmed: true);
        await InsertUser(conn, "unconfirmed", confirmed: false);
        await conn.SettingsSetAsync(SettingsKeys.NewBuildsEnabled, "false");

        await tester.RunRemainingScripts();

        Assert.Empty(await conn.QueryAsync<string>("SELECT user_id FROM build_whitelist"));
        Assert.Null(await conn.SettingsGetAsync(SettingsKeys.NewBuildsWhitelist));
        Assert.Equal("false", await conn.SettingsGetAsync(SettingsKeys.NewBuildsEnabled));
        Assert.Equal(2, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM \"AspNetUsers\""));
    }

    [Fact]
    public async Task WhitelistRequiresAnExistingUniqueAccountAndCascadesItsDeletion()
    {
        await using var tester = CreateMigrationTester("BuildWhitelistConstraints");
        await tester.RunScriptsUntil("25.BuildWhitelist");
        await tester.RunRemainingScripts();
        await using var conn = await tester.Open();
        await InsertUser(conn, "authorized", confirmed: true);
        await conn.ExecuteAsync("INSERT INTO build_whitelist (user_id) VALUES ('authorized')");

        var duplicate = await Assert.ThrowsAsync<PostgresException>(() =>
            conn.ExecuteAsync("INSERT INTO build_whitelist (user_id) VALUES ('authorized')"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        var nonexistent = await Assert.ThrowsAsync<PostgresException>(() =>
            conn.ExecuteAsync("INSERT INTO build_whitelist (user_id) VALUES ('nonexistent')"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, nonexistent.SqlState);
        Assert.Equal("authorized", await conn.QuerySingleAsync<string>("SELECT user_id FROM build_whitelist"));

        await conn.ExecuteAsync("DELETE FROM \"AspNetUsers\" WHERE \"Id\" = 'authorized'");

        Assert.Empty(await conn.QueryAsync<string>("SELECT user_id FROM build_whitelist"));
    }

    private static Task InsertUser(NpgsqlConnection conn, string userId, bool confirmed) =>
        conn.ExecuteAsync(
            """
            INSERT INTO "AspNetUsers"
                ("Id", "UserName", "NormalizedUserName", "Email", "NormalizedEmail", "EmailConfirmed",
                 "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
            VALUES
                (@userId, @userId, UPPER(@userId), @email, UPPER(@email), @confirmed, FALSE, FALSE, FALSE, 0)
            """, new { userId, email = $"{userId}@example.test", confirmed });
}
