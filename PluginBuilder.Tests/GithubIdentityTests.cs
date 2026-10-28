using Dapper;
using Newtonsoft.Json.Linq;
using Npgsql;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests;

public class GithubIdentityTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    private const string UserId = "legacy-github-user";
    private const string ProofUrl = "https://gist.github.com/old-login/a1b2c3";

    [Fact]
    public async Task MigrationPreservesLegacyProofWithoutGuessingAnId()
    {
        await using var tester = CreateMigrationTester();
        await tester.RunScriptsUntil("30.GithubIdentity");
        await using var conn = await tester.Open();
        await SeedUser(conn);
        await tester.RunRemainingScripts();

        Assert.Null(await conn.QuerySingleAsync<long?>("SELECT \"GithubUserId\" FROM \"AspNetUsers\" WHERE \"Id\" = @UserId", new { UserId }));
        Assert.True(await conn.IsGithubAccountVerified(UserId));
        Assert.Equal("old-login", (await conn.GetAccountDetailSettings(UserId))!.Github);
        var invalid = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(
            "UPDATE \"AspNetUsers\" SET \"GithubUserId\" = 0 WHERE \"Id\" = @UserId", new { UserId }));
        Assert.Equal(PostgresErrorCodes.CheckViolation, invalid.SqlState);
    }

    [Fact]
    public async Task VerificationAndAStaleProfileEditKeepIdentityProofAndOtherFieldsTogether()
    {
        await using var tester = CreateMigrationTester("GithubStaleProfile");
        await tester.RunScriptsUntil("30.GithubIdentity");
        await tester.RunRemainingScripts();
        await using var conn = await tester.Open();
        await SeedUser(conn);
        const string newProof = "https://gist.github.com/CurrentLogin/abcdef";
        Assert.Equal(0, await conn.VerifyGithubAccount(UserId, newProof, new VerifiedGithubIdentity(5_000_000_001, "CurrentLogin")));
        await conn.ExecuteAsync("UPDATE \"AspNetUsers\" SET \"GithubUserId\" = 5000000001 WHERE \"Id\" = @UserId", new { UserId });
        var staleSettings = (await conn.GetAccountDetailSettings(UserId))!;

        Assert.Equal(1, await conn.VerifyGithubAccount(UserId, newProof, new VerifiedGithubIdentity(5_000_000_001, "CurrentLogin")));
        var verificationEvent = JObject.Parse(await conn.QuerySingleAsync<string>(
            "SELECT data::text FROM admin_events WHERE type = 'user.github_verified' ORDER BY id DESC LIMIT 1"));
        Assert.Equal("CurrentLogin", verificationEvent.Value<string>("github"));
        Assert.Equal(newProof, verificationEvent.Value<string>("gistUrl"));
        Assert.Equal("original-twitter", (await conn.GetAccountDetailSettings(UserId))!.Twitter);

        staleSettings.Twitter = "updated-twitter";
        staleSettings.Github = "untrusted-profile-value";
        await conn.SetAccountDetailSettings(staleSettings, UserId);
        Assert.Equal(5_000_000_001, await conn.QuerySingleAsync<long?>("SELECT \"GithubUserId\" FROM \"AspNetUsers\" WHERE \"Id\" = @UserId", new { UserId }));
        var settings = (await conn.GetAccountDetailSettings(UserId))!;
        Assert.Equal("CurrentLogin", settings.Github);
        Assert.Equal("updated-twitter", settings.Twitter);
        Assert.Equal(newProof, await StoredProof(conn));

        // Multiple local accounts can prove the same GitHub identity; a later moderation PR handles them together.
        await SeedUser(conn, "another-user");
        await conn.ExecuteAsync("UPDATE \"AspNetUsers\" SET \"GithubGistUrl\" = NULL WHERE \"Id\" = 'another-user'");
        Assert.Equal(1, await conn.VerifyGithubAccount("another-user", newProof, new VerifiedGithubIdentity(5_000_000_001, "CurrentLogin")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnverifiedProfileEditsPreserveAVerificationSavedAfterTheInitialRead(bool identityKnown)
    {
        await using var tester = CreateMigrationTester("GithubCleanupRace");
        await tester.RunScriptsUntil("30.GithubIdentity");
        await tester.RunRemainingScripts();
        await using var conn = await tester.Open();
        await SeedUser(conn);
        await conn.ExecuteAsync("UPDATE \"AspNetUsers\" SET \"GithubGistUrl\" = NULL WHERE \"Id\" = @UserId", new { UserId });
        Assert.False(await conn.IsGithubAccountVerified(UserId));
        var staleSettings = (await conn.GetAccountDetailSettings(UserId))!;

        // Another request completes verification after the initial decision to show the proof form.
        await using (var verifier = await tester.Open())
        {
            if (identityKnown)
                await verifier.VerifyGithubAccount(UserId, ProofUrl, new VerifiedGithubIdentity(1234, "VerifiedOwner"));
            else
                await verifier.ExecuteAsync("""
                    UPDATE "AspNetUsers" SET "GithubGistUrl" = @ProofUrl,
                        "AccountDetail" = jsonb_set("AccountDetail", '{github}', '"VerifiedOwner"'::jsonb)
                    WHERE "Id" = @UserId
                    """, new { UserId, ProofUrl });
        }

        Assert.Equal(0, await conn.ClearUnverifiedGithubHandle(UserId));
        staleSettings.Github = "untrusted-profile-value";
        await conn.SetAccountDetailSettings(staleSettings, UserId);
        Assert.True(await conn.IsGithubAccountVerified(UserId));
        Assert.Equal("VerifiedOwner", (await conn.GetAccountDetailSettings(UserId))!.Github);
        Assert.Equal(identityKnown ? 1234L : (long?)null,
            await conn.QuerySingleAsync<long?>("SELECT \"GithubUserId\" FROM \"AspNetUsers\" WHERE \"Id\" = @UserId", new { UserId }));
    }

    private static Task SeedUser(NpgsqlConnection conn, string userId = UserId) => conn.ExecuteAsync("""
        INSERT INTO "AspNetUsers" ("Id", "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount", "GithubGistUrl", "AccountDetail")
        VALUES (@userId, true, false, false, false, 0, @ProofUrl, '{"github":"old-login","twitter":"original-twitter"}'::jsonb)
        """, new { userId, ProofUrl });

    private static Task<string> StoredProof(NpgsqlConnection conn) =>
        conn.QuerySingleAsync<string>("SELECT \"GithubGistUrl\" FROM \"AspNetUsers\" WHERE \"Id\" = @UserId", new { UserId });
}
