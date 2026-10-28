using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json.Linq;
using Npgsql;
using PluginBuilder.Builds.Services;
using PluginBuilder.DataModels;
using PluginBuilder.HostedServices;
using PluginBuilder.Services;
using PluginBuilder.Util;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class GithubBanTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    private const string Password = "test-password:123";
    private const string ProofUrl = "https://gist.github.com/old-login/abcdef123456";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BurnCancelsLinkedBuildsAndRevokeLeavesAccountsLocked(bool usernameBan)
    {
        var requests = 0;
        var github = new GithubHandler { Reply = () => { requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)); } };
        await using var tester = await StartServer(github);
        var (admin, actor, tokenId) = await AdminClient(tester);
        using var adminClient = admin;
        var owner = await User(tester);
        await SetIdentity(tester, owner, usernameBan ? null : 123);
        var linked = await User(tester);
        await SetIdentity(tester, linked, 123, "Old-Login");
        var legacy = await User(tester);
        await SetIdentity(tester, legacy, null, "OLD-LOGIN");
        var unrelated = await User(tester, 456); // An ID ban must ignore the same display login.
        if (usernameBan) await SetIdentity(tester, unrelated, 456, "another-owner");
        var unverified = await User(tester);
        await SetIdentity(tester, unverified, null, proof: "");
        var locked = usernameBan ? new[] { owner, linked, legacy } : new[] { owner, linked };
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.ExecuteAsync("""UPDATE "AspNetUsers" SET "SignupIp" = '203.0.113.7'::inet WHERE "Id" = @owner""", new { owner });
        var slug = new PluginSlug("github-burn");
        await conn.NewPlugin(slug, owner);
        await conn.NewBuild(slug, new PluginBuildParameters(ServerTester.RepoUrl), owner);
        var running = await conn.NewBuild(slug, new PluginBuildParameters(ServerTester.RepoUrl), linked);
        await conn.UpdateBuild(new FullBuildId(slug, running), BuildStates.Running, null);
        var finished = await conn.NewBuild(slug, new PluginBuildParameters(ServerTester.RepoUrl), owner);
        await conn.UpdateBuild(new FullBuildId(slug, finished), BuildStates.Uploaded, null);
        await conn.NewBuild(slug, new PluginBuildParameters(ServerTester.RepoUrl), unrelated);
        var registry = tester.GetService<BuildCancellationRegistry>();
        var runningId = new FullBuildId(slug, running);
        var cancellation = registry.Register(runningId, CancellationToken.None);
        try
        {
            Assert.Equal(HttpStatusCode.NotFound,
                (await adminClient.PostAsJsonAsync("/api/v1/admin/users/missing/burn", new { reason = "x" })).StatusCode);
            var response = await adminClient.PostAsJsonAsync($"/api/v1/admin/users/{owner}/burn", new { reason = "network probe" });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = JObject.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(usernameBan ? (long?)null : 123, body.Value<long?>("githubUserId"));
            Assert.Equal(usernameBan ? (long?)null : 123, await conn.ExecuteScalarAsync<long?>("""SELECT "GithubUserId" FROM "AspNetUsers" WHERE "Id" = @owner""", new { owner }));
            Assert.Equal("old-login", body.Value<string>("githubLogin"));
            Assert.Equal(locked.Order(), body["lockedUserIds"]!.Values<string>().Order());
            Assert.Empty(body["warnings"]!);
            Assert.Equal(locked.Order(),
                (await conn.QueryAsync<string>("SELECT user_id FROM admin_account_locks")).Order());
            Assert.True(cancellation.IsCancellationRequested);
            Assert.Equal(new[] { "failed", "failed", "uploaded", "queued" },
                await conn.QueryAsync<string>("SELECT state FROM builds WHERE plugin_slug = @slug ORDER BY id", new { slug = slug.ToString() }));
            var ban = Assert.Single(JArray.Parse(await adminClient.GetStringAsync("/api/v1/admin/github-bans")));
            Assert.Equal(usernameBan ? (long?)null : 123, ban.Value<long?>("githubUserId"));
            Assert.Equal("old-login", ban.Value<string>("githubLogin"));
            Assert.Equal("network probe", ban.Value<string>("reason"));
            var audit = JObject.Parse(await conn.QuerySingleAsync<string>("SELECT data::text FROM admin_events WHERE type = 'user.burned'"));
            Assert.Equal(actor, audit.Value<string>("by"));
            Assert.Equal(tokenId, audit.Value<string>("tokenId"));

            const string linkedLockSnapshot = """
                SELECT jsonb_build_object('lock', to_jsonb(l), 'stamp', u."SecurityStamp",
                  'events', (SELECT count(*) FROM admin_events WHERE type = 'user.locked' AND data->>'userId' = u."Id"))::text
                FROM admin_account_locks l JOIN "AspNetUsers" u ON u."Id" = l.user_id WHERE l.user_id = @linked
                """;
            var preservedLock = await conn.QuerySingleAsync<string>(linkedLockSnapshot, new { linked });

            // Unlocking an account does not revoke the identity ban: another build attempt enforces it again.
            Assert.Equal(HttpStatusCode.OK, (await adminClient.PostAsJsonAsync($"/api/v1/admin/users/{owner}/unlock", new { reason = "separate action" })).StatusCode);
            Assert.Equal("This account is suspended from creating builds.", await tester.GetService<GithubIdentityService>().GetBuildRestriction(owner, true));
            Assert.Equal(preservedLock, await conn.QuerySingleAsync<string>(linkedLockSnapshot, new { linked }));
            Assert.Equal(locked.Length, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_account_locks"));

            Assert.Equal(HttpStatusCode.OK, (await adminClient.PostAsJsonAsync($"/api/v1/admin/github-bans/{ban.Value<long>("id")}/revoke", new { reason = "reviewed" })).StatusCode);
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM github_bans"));
            Assert.Equal(locked.Length, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_account_locks"));
            Assert.Equal("old-login", await conn.QuerySingleAsync<string>("SELECT data->>'githubLogin' FROM admin_events WHERE type = 'github.ban_revoked'"));

            Assert.Equal(HttpStatusCode.OK, (await adminClient.PostAsJsonAsync($"/api/v1/admin/users/{unrelated}/lock", new { reason = "ordinary lock" })).StatusCode);
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM github_bans"));
            Assert.Equal(0, requests);
        }
        finally { registry.Unregister(runningId, cancellation); }
    }

    [Fact]
    public async Task BurnWithoutVerifiedIdentityReportsTheMissingBanAndProtectsAdminAccounts()
    {
        await using var tester = await StartServer();
        var (admin, adminId, _) = await AdminClient(tester);
        using var client = admin;
        var legacy = await User(tester);
        await SetIdentity(tester, legacy, null, proof: "");
        var partial = await client.PostAsJsonAsync($"/api/v1/admin/users/{legacy}/burn", new { reason = "confirmed probe" });
        Assert.Equal(HttpStatusCode.OK, partial.StatusCode);
        var body = JObject.Parse(await partial.Content.ReadAsStringAsync());
        Assert.Equal(JTokenType.Null, body["githubUserId"]!.Type);
        Assert.NotEmpty(body["warnings"]!);
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_account_locks WHERE user_id = @legacy", new { legacy }));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM github_bans"));
        Assert.False(await conn.IsGithubAccountVerified(legacy));

        await SetIdentity(tester, adminId, 987);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsJsonAsync($"/api/v1/admin/users/{adminId}/burn", new { reason = "x" })).StatusCode);
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM github_bans"));
        var linked = await User(tester, 987);
        var burned = await client.PostAsJsonAsync($"/api/v1/admin/users/{linked}/burn", new { reason = "linked probe" });
        Assert.Equal(HttpStatusCode.OK, burned.StatusCode);
        var result = JObject.Parse(await burned.Content.ReadAsStringAsync());
        Assert.Equal(adminId, Assert.Single(result["skippedAdminUserIds"]!.Values<string>()));
        Assert.NotEmpty(result["warnings"]!);
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_account_locks WHERE user_id = @adminId", new { adminId }));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_account_locks WHERE user_id = @linked", new { linked }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyAValidUserBoundProofOfABannedIdentityLocksTheAccount(bool usernameBan)
    {
        var gistUrl = "HTTPS://GIST.GITHUB.COM:443/old-login/abcdef123456?file=proof.txt#" + new string('x', 120);
        var github = new GithubHandler();
        await using var tester = await StartServer(github);
        var linked = await User(tester, 444);
        var target = await User(tester);
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.ExecuteAsync("INSERT INTO github_bans (github_user_id, github_login, reason) VALUES (@id, 'OLD-LOGIN', 'probe')",
            new { id = usernameBan ? (long?)null : 444 });
        var slug = new PluginSlug("github-banned-proof");
        await conn.NewPlugin(slug, target);
        await conn.NewBuild(slug, new PluginBuildParameters(ServerTester.RepoUrl), target);
        await conn.NewBuild(slug, new PluginBuildParameters(ServerTester.RepoUrl), linked);
        using var browser = HttpTestHelpers.CreateBrowser(tester);
        await HttpTestHelpers.LogIn(browser, await Email(conn, target), Password);
        var page = await browser.GetStringAsync("/account/VerifyGithubAccount");
        github.Reply = () => Task.FromResult(Proof(444, "old-login", "another-account"));
        using var invalid = await Submit(browser, page, gistUrl);
        Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_account_locks"));
        var (admin, _, _) = await AdminClient(tester);
        using (admin)
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/admin/users/{linked}/lock",
                new { reason = "temporary lock", until = DateTimeOffset.UtcNow.AddHours(1) })).StatusCode);
        github.Reply = () => Task.FromResult(Proof(444, "old-login", target));
        using var valid = await Submit(browser, await invalid.Content.ReadAsStringAsync(), gistUrl);
        Assert.Equal(HttpStatusCode.Redirect, valid.StatusCode);
        Assert.Equal("/errors/403", new Uri(browser.BaseAddress!, valid.Headers.Location!).AbsolutePath);
        Assert.Equal(new[] { target, linked }.Order(),
            (await conn.QueryAsync<string>("SELECT user_id FROM admin_account_locks")).Order());
        Assert.True(await conn.ExecuteScalarAsync<bool>("SELECT locked_until = 'infinity' FROM admin_account_locks WHERE user_id = @linked", new { linked }));
        Assert.Equal(444, await conn.ExecuteScalarAsync<long?>("SELECT \"GithubUserId\" FROM \"AspNetUsers\" WHERE \"Id\" = @target", new { target }));
        Assert.Equal(ProofUrl, await conn.ExecuteScalarAsync<string>("SELECT \"GithubGistUrl\" FROM \"AspNetUsers\" WHERE \"Id\" = @target", new { target }));
        Assert.Equal(2, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM builds WHERE state = 'failed'"));
        using var signedOut = await browser.GetAsync("/account/details");
        Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
        Assert.Equal("/login", new Uri(browser.BaseAddress!, signedOut.Headers.Location!).AbsolutePath);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ALegacyProofCanRecordItsIdUnlessItsLoginIsBanned(bool banned)
    {
        await using var tester = await StartServer();
        var legacy = await User(tester);
        await SetIdentity(tester, legacy, null);
        var linked = await User(tester);
        await SetIdentity(tester, linked, 444, "OLD-LOGIN");
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        if (banned)
            await conn.ExecuteAsync("INSERT INTO github_bans (github_login, reason) VALUES ('Old-Login', 'probe')");
        const string newProof = "https://gist.github.com/old-login/fedcba";

        var accepted = await tester.GetService<GithubIdentityService>().SaveProof(legacy, newProof,
            new VerifiedGithubIdentity(999, "OLD-LOGIN"));

        Assert.Equal(!banned, accepted);
        Assert.Equal(banned ? (long?)null : 999, await conn.ExecuteScalarAsync<long?>(
            "SELECT \"GithubUserId\" FROM \"AspNetUsers\" WHERE \"Id\" = @legacy", new { legacy }));
        Assert.Equal(banned ? ProofUrl : newProof, await conn.ExecuteScalarAsync<string>(
            "SELECT \"GithubGistUrl\" FROM \"AspNetUsers\" WHERE \"Id\" = @legacy", new { legacy }));
        Assert.Equal(banned ? "old-login" : "OLD-LOGIN", (await conn.GetAccountDetailSettings(legacy))!.Github);
        Assert.Equal((banned ? new[] { legacy, linked } : Array.Empty<string>()).Order(),
            (await conn.QueryAsync<string>("SELECT user_id FROM admin_account_locks")).Order());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalProofDoesNotWaitForModerationAndTheNextBuildChecksTheBan(bool usernameBan)
    {
        await using var tester = await StartServer();
        var target = await User(tester);
        var connections = tester.GetService<DBConnectionFactory>();
        await using var blocking = await connections.Open();
        await using var observer = await connections.Open();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using (var tx = await blocking.BeginTransactionAsync(timeout.Token))
        {
            await AccountModerationService.Serialize(blocking, tx, timeout.Token);
            await blocking.ExecuteAsync(new CommandDefinition("""
                INSERT INTO github_bans (github_user_id, github_login, reason) VALUES (@id, 'OLD-LOGIN', 'pending ban')
                """, new { id = usernameBan ? (long?)null : 444 }, tx, cancellationToken: timeout.Token));
            Assert.True(await tester.GetService<GithubIdentityService>().SaveProof(target, ProofUrl,
                new VerifiedGithubIdentity(444, "old-login"), timeout.Token).WaitAsync(timeout.Token));
            Assert.Equal(444, await observer.ExecuteScalarAsync<long?>(
                "SELECT \"GithubUserId\" FROM \"AspNetUsers\" WHERE \"Id\" = @target", new { target }));
            await tx.CommitAsync(timeout.Token);
        }

        var slug = new PluginSlug("github-proof-race");
        await observer.NewPlugin(slug, target);
        var rejected = await Assert.ThrowsAsync<BuildServiceException>(() => observer.NewBuildWithinUserLimit(slug,
            new PluginBuildParameters(ServerTester.RepoUrl), target, true));
        Assert.Equal(GithubIdentityService.SuspendedMessage, rejected.Message);
        Assert.Equal(0, await observer.ExecuteScalarAsync<int>("SELECT count(*) FROM builds"));
    }

    [Fact]
    public async Task ConcurrentFirstProofsCannotReplaceEachOthersIdentity()
    {
        await using var tester = await StartServer();
        var target = await User(tester);
        var connections = tester.GetService<DBConnectionFactory>();
        await using var blocking = await connections.Open();
        await using var observer = await connections.Open();
        await using var tx = await blocking.BeginTransactionAsync();
        await blocking.ExecuteAsync("SELECT 1 FROM \"AspNetUsers\" WHERE \"Id\" = @target FOR UPDATE", new { target }, tx);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var service = tester.GetService<GithubIdentityService>();
        const string secondProof = "https://gist.github.com/new-owner/fedcba";
        var first = service.SaveProof(target, ProofUrl, new VerifiedGithubIdentity(444, "old-login"), timeout.Token);
        var second = service.SaveProof(target, secondProof, new VerifiedGithubIdentity(555, "new-owner"), timeout.Token);
        try
        {
            // Both requests must have read the unlinked account before either UPDATE can finish.
            while (await observer.ExecuteScalarAsync<int>(new CommandDefinition("""
                       SELECT count(*) FROM pg_stat_activity WHERE datname = current_database()
                         AND wait_event_type = 'Lock' AND query LIKE 'UPDATE "AspNetUsers" SET "GithubGistUrl"%'
                       """, cancellationToken: timeout.Token)) < 2)
            {
                Assert.False(first.IsCompleted || second.IsCompleted);
                await Task.Delay(10, timeout.Token);
            }
        }
        finally { await tx.RollbackAsync(); }

        var firstError = await Record.ExceptionAsync(async () => Assert.True(await first.WaitAsync(timeout.Token)));
        var secondError = await Record.ExceptionAsync(async () => Assert.True(await second.WaitAsync(timeout.Token)));
        Assert.Equal(1, new[] { firstError, secondError }.Count(error => error is null));
        Assert.IsType<InvalidOperationException>(firstError ?? secondError);
        Assert.Equal(firstError is null ? 444 : 555, await observer.ExecuteScalarAsync<long?>(
            "SELECT \"GithubUserId\" FROM \"AspNetUsers\" WHERE \"Id\" = @target", new { target }));
        Assert.Equal(firstError is null ? ProofUrl : secondProof, await observer.QuerySingleAsync<string>(
            "SELECT \"GithubGistUrl\" FROM \"AspNetUsers\" WHERE \"Id\" = @target", new { target }));
        Assert.Equal(firstError is null ? "old-login" : "new-owner", (await observer.GetAccountDetailSettings(target))!.Github);
    }

    private async Task<ServerTester> StartServer(GithubHandler? github = null)
    {
        var tester = Create(nameof(GithubBanTests));
        tester.ReuseDatabase = false;
        tester.ConfigureServices = services =>
        {
            foreach (var service in services.Where(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType != typeof(DatabaseStartupHostedService)).ToArray())
                services.Remove(service);
            services.AddHttpClient(HttpClientNames.GitHub).ConfigurePrimaryHttpMessageHandler(() => github ?? new GithubHandler());
        };
        await tester.Start();
        return tester;
    }

    private static async Task<(HttpClient Client, string UserId, string TokenId)> AdminClient(ServerTester tester)
    {
        var userId = await User(tester);
        using var scope = tester.WebApp.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var admin = (await users.FindByIdAsync(userId))!;
        Assert.True((await users.AddToRoleAsync(admin, Roles.ServerAdmin)).Succeeded);
        using var issuer = tester.CreateHttpClient().SetBasicAuth(admin.Email!, Password);
        using var response = await issuer.PostAsJsonAsync("/api/v1/admin/access-tokens", new { name = "github ban test", expiresInDays = 1 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var token = JObject.Parse(await response.Content.ReadAsStringAsync());
        var client = tester.CreateHttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value<string>("token"));
        return (client, userId, token.Value<string>("id")!);
    }

    private static async Task<string> User(ServerTester tester, long? githubId = null)
    {
        var userId = await tester.CreateFakeUserAsync(password: Password, githubVerified: false);
        if (githubId is not null) await SetIdentity(tester, userId, githubId);
        return userId;
    }

    private static async Task SetIdentity(ServerTester tester, string userId, long? id, string login = "old-login", string proof = ProofUrl)
    {
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.ExecuteAsync("""
            UPDATE "AspNetUsers" SET "GithubUserId" = @id, "GithubGistUrl" = @proof,
              "AccountDetail" = jsonb_build_object('github', @login::text) WHERE "Id" = @userId
            """, new { userId, id, proof, login });
    }

    private static Task<string> Email(NpgsqlConnection conn, string userId) =>
        conn.QuerySingleAsync<string>("SELECT \"Email\" FROM \"AspNetUsers\" WHERE \"Id\" = @userId", new { userId });

    private static Task<HttpResponseMessage> Submit(HttpClient browser, string page, string gistUrl) => browser.PostAsync("/account/VerifyGithubAccount", new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["GistUrl"] = gistUrl, ["__RequestVerificationToken"] = HttpTestHelpers.AntiforgeryToken(page)
    }));

    private static HttpResponseMessage Proof(long id, string login, string token) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(new JObject
        {
            ["owner"] = new JObject { ["id"] = id, ["login"] = login },
            ["files"] = new JObject { ["proof.txt"] = new JObject { ["content"] = token } }
        }.ToString(), System.Text.Encoding.UTF8, "application/json")
    };

    private sealed class GithubHandler : HttpMessageHandler
    {
        public Func<Task<HttpResponseMessage>> Reply { get; set; } = () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Reply().WaitAsync(cancellationToken);
    }
}
