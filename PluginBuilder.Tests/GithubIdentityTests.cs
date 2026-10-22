using System.Net;
using Dapper;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using Npgsql;
using PluginBuilder.DataModels;
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

        Assert.Null(await conn.GetGithubUserId(UserId));
        Assert.True(await conn.IsGithubAccountVerified(UserId));
        Assert.Equal("old-login", (await conn.GetAccountDetailSettings(UserId))!.Github);
        var invalid = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(
            "UPDATE \"AspNetUsers\" SET \"GithubUserId\" = 0 WHERE \"Id\" = @UserId", new { UserId }));
        Assert.Equal(PostgresErrorCodes.CheckViolation, invalid.SqlState);
    }

    [Fact]
    public async Task VerificationAndAStaleProfileEditKeepIdentityProofAndOtherFieldsTogether()
    {
        await using var tester = CreateMigrationTester();
        await tester.RunScriptsUntil("30.GithubIdentity");
        await tester.RunRemainingScripts();
        await using var conn = await tester.Open();
        await SeedUser(conn);
        var staleSettings = (await conn.GetAccountDetailSettings(UserId))!;
        const string newProof = "https://gist.github.com/CurrentLogin/abcdef";

        Assert.Equal(1, await conn.VerifyGithubAccount(UserId, newProof, new VerifiedGithubIdentity(5_000_000_001, "CurrentLogin")));
        var verificationEvent = JObject.Parse(await conn.QuerySingleAsync<string>(
            "SELECT data::text FROM admin_events WHERE type = 'user.github_verified' ORDER BY id DESC LIMIT 1"));
        Assert.Equal("CurrentLogin", verificationEvent.Value<string>("github"));
        Assert.Equal(newProof, verificationEvent.Value<string>("gistUrl"));
        Assert.Equal("original-twitter", (await conn.GetAccountDetailSettings(UserId))!.Twitter);

        staleSettings.Twitter = "updated-twitter";
        staleSettings.Github = "untrusted-profile-value";
        await conn.SetAccountDetailSettings(staleSettings, UserId);
        Assert.Equal(5_000_000_001, await conn.GetGithubUserId(UserId));
        var settings = (await conn.GetAccountDetailSettings(UserId))!;
        Assert.Equal("CurrentLogin", settings.Github);
        Assert.Equal("updated-twitter", settings.Twitter);
        Assert.Equal(newProof, await StoredProof(conn));

        // Multiple local accounts can prove the same GitHub identity; a later moderation PR handles them together.
        await SeedUser(conn, "another-user");
        Assert.Equal(1, await conn.VerifyGithubAccount("another-user", newProof, new VerifiedGithubIdentity(5_000_000_001, "CurrentLogin")));
    }

    [Fact]
    public async Task RecoveryUsesTheStoredGistAndCurrentOwnerAfterARename()
    {
        await using var tester = CreateMigrationTester();
        await tester.RunScriptsUntil("30.GithubIdentity");
        await tester.RunRemainingScripts();
        await using var conn = await tester.Open();
        await SeedUser(conn);
        using var handler = new ProofHandler(_ => Task.FromResult(Proof(1234, "RenamedOwner")));
        await using var factory = ConnectionFactory(conn);
        var service = new GithubIdentityService(factory, new ExternalAccountVerificationService(new StubFactory(handler)));

        Assert.Equal(1234, await service.RecoverUserId(UserId));
        Assert.Equal("RenamedOwner", (await conn.GetAccountDetailSettings(UserId))!.Github);
        Assert.Equal(ProofUrl, await StoredProof(conn));
        Assert.Equal(new Uri("https://api.github.com/gists/a1b2c3"), Assert.Single(handler.Requests));
        // A known ID survives a deleted proof and does not depend on another GitHub round-trip.
        Assert.Equal(1234, await service.RecoverUserId(UserId));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ADeletedProofPreservesLegacyVerification()
    {
        await using var tester = CreateMigrationTester();
        await tester.RunScriptsUntil("30.GithubIdentity");
        await tester.RunRemainingScripts();
        await using var conn = await tester.Open();
        await SeedUser(conn);
        using var handler = new ProofHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        await using var factory = ConnectionFactory(conn);
        var service = new GithubIdentityService(factory, new ExternalAccountVerificationService(new StubFactory(handler)));

        Assert.Null(await service.RecoverUserId(UserId));
        Assert.True(await conn.IsGithubAccountVerified(UserId));
        Assert.Equal("old-login", (await conn.GetAccountDetailSettings(UserId))!.Github);
        Assert.Equal(ProofUrl, await StoredProof(conn));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryCannotOverwriteAProofOrIdentityReplacedWhileGithubResponds(bool newIdentityKnown)
    {
        await using var tester = CreateMigrationTester();
        await tester.RunScriptsUntil("30.GithubIdentity");
        await tester.RunRemainingScripts();
        await using var conn = await tester.Open();
        await SeedUser(conn);
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new ProofHandler(async cancellationToken =>
        {
            requested.SetResult();
            return await response.Task.WaitAsync(cancellationToken);
        });
        await using var factory = ConnectionFactory(conn);
        var service = new GithubIdentityService(factory, new ExternalAccountVerificationService(new StubFactory(handler)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var recovering = service.RecoverUserId(UserId, timeout.Token);
        try
        {
            await requested.Task.WaitAsync(timeout.Token);
            const string newProof = "https://gist.github.com/other-owner/def123";
            if (newIdentityKnown)
                await conn.VerifyGithubAccount(UserId, newProof, new VerifiedGithubIdentity(5678, "other-owner"));
            else
                await conn.ExecuteAsync("UPDATE \"AspNetUsers\" SET \"GithubGistUrl\" = @newProof WHERE \"Id\" = @UserId", new { newProof, UserId });
            response.SetResult(Proof(1234, "RenamedOwner"));

            Assert.Equal(newIdentityKnown ? 5678L : (long?)null, await recovering);
            Assert.Equal(newProof, await StoredProof(conn));
            Assert.Equal(newIdentityKnown ? "other-owner" : "old-login", (await conn.GetAccountDetailSettings(UserId))!.Github);
        }
        finally
        {
            await timeout.CancelAsync();
            try { await recovering; }
            catch (OperationCanceledException) { }
        }
    }

    private static Task SeedUser(NpgsqlConnection conn, string userId = UserId) => conn.ExecuteAsync("""
        INSERT INTO "AspNetUsers" ("Id", "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount", "GithubGistUrl", "AccountDetail")
        VALUES (@userId, true, false, false, false, 0, @ProofUrl, '{"github":"old-login","twitter":"original-twitter"}'::jsonb)
        """, new { userId, ProofUrl });

    private static Task<string> StoredProof(NpgsqlConnection conn) =>
        conn.QuerySingleAsync<string>("SELECT \"GithubGistUrl\" FROM \"AspNetUsers\" WHERE \"Id\" = @UserId", new { UserId });

    private static DBConnectionFactory ConnectionFactory(NpgsqlConnection conn) => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["POSTGRES"] = conn.ConnectionString }).Build());

    private static HttpResponseMessage Proof(long id, string login) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(new JObject
        {
            ["owner"] = new JObject { ["id"] = id, ["login"] = login },
            ["files"] = new JObject { ["proof.txt"] = new JObject { ["content"] = UserId } }
        }.ToString())
    };

    private sealed class ProofHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<Uri?> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri);
            return respond(cancellationToken);
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(HttpClientNames.GitHub, name);
            return new HttpClient(handler, false) { BaseAddress = new Uri("https://api.github.com/") };
        }
    }
}
