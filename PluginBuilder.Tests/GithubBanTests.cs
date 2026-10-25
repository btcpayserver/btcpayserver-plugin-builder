using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json.Linq;
using Npgsql;
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

    [Fact]
    public async Task BurnUsesTheVerifiedIdCancelsLinkedBuildsAndRevokeLeavesAccountsLocked()
    {
        var github = new GithubHandler();
        await using var tester = await StartServer(github);
        var (admin, actor, tokenId) = await AdminClient(tester);
        using var adminClient = admin;
        var owner = await User(tester);
        await SetIdentity(tester, owner, null);
        github.Reply = () => Task.FromResult(Proof(123, "renamed-owner", owner));
        var linked = await User(tester, 123);
        var unrelated = await User(tester, 456); // The same display login is not an identity match.
        var unverified = await User(tester);
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.ExecuteAsync("""
            UPDATE "AspNetUsers" SET "AccountDetail" = '{"github":"renamed-owner"}'::jsonb WHERE "Id" = ANY(@userIds)
            """, new { userIds = new[] { unrelated, unverified } });
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
            Assert.Equal(123, body.Value<long>("githubUserId"));
            Assert.Equal(123, await conn.ExecuteScalarAsync<long?>("""SELECT "GithubUserId" FROM "AspNetUsers" WHERE "Id" = @owner""", new { owner }));
            Assert.Equal("renamed-owner", (await conn.GetAccountDetailSettings(owner))!.Github);
            Assert.Equal(new[] { owner, linked }.Order(), body["lockedUserIds"]!.Values<string>().Order());
            Assert.Empty(body["warnings"]!);
            Assert.Equal(new[] { owner, linked }.Order(),
                (await conn.QueryAsync<string>("SELECT user_id FROM admin_account_locks")).Order());
            Assert.True(cancellation.IsCancellationRequested);
            Assert.Equal(new[] { "failed", "failed", "uploaded", "queued" },
                await conn.QueryAsync<string>("SELECT state FROM builds WHERE plugin_slug = @slug ORDER BY id", new { slug = slug.ToString() }));
            var ban = Assert.Single(JArray.Parse(await adminClient.GetStringAsync("/api/v1/admin/github-bans")));
            Assert.Equal(123, ban.Value<long>("githubUserId"));
            Assert.Equal("network probe", ban.Value<string>("reason"));
            var audit = JObject.Parse(await conn.QuerySingleAsync<string>("SELECT data::text FROM admin_events WHERE type = 'user.burned'"));
            Assert.Equal(actor, audit.Value<string>("by"));
            Assert.Equal(tokenId, audit.Value<string>("tokenId"));

            // Unlocking an account does not revoke the identity ban: another build attempt enforces it again.
            Assert.Equal(HttpStatusCode.OK, (await adminClient.PostAsJsonAsync($"/api/v1/admin/users/{owner}/unlock", new { reason = "separate action" })).StatusCode);
            Assert.False(await tester.GetService<GithubIdentityService>().CanBuild(owner, true));
            Assert.Equal(2, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_account_locks"));

            Assert.Equal(HttpStatusCode.OK, (await adminClient.PostAsJsonAsync("/api/v1/admin/github-bans/123/revoke", new { reason = "reviewed" })).StatusCode);
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM github_bans"));
            Assert.Equal(2, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_account_locks"));
            Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_events WHERE type = 'github.ban_revoked'"));

            Assert.Equal(HttpStatusCode.OK, (await adminClient.PostAsJsonAsync($"/api/v1/admin/users/{unrelated}/lock", new { reason = "ordinary lock" })).StatusCode);
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM github_bans"));
        }
        finally { registry.Unregister(runningId, cancellation); }
    }

    [Fact]
    public async Task BurnReportsAnUnavailableLegacyProofAndProtectsAdminAccounts()
    {
        await using var tester = await StartServer();
        var (admin, adminId, _) = await AdminClient(tester);
        using var client = admin;
        var legacy = await User(tester);
        await SetIdentity(tester, legacy, null);
        var partial = await client.PostAsJsonAsync($"/api/v1/admin/users/{legacy}/burn", new { reason = "confirmed probe" });
        Assert.Equal(HttpStatusCode.OK, partial.StatusCode);
        var body = JObject.Parse(await partial.Content.ReadAsStringAsync());
        Assert.Equal(JTokenType.Null, body["githubUserId"]!.Type);
        Assert.NotEmpty(body["warnings"]!);
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_account_locks WHERE user_id = @legacy", new { legacy }));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM github_bans"));
        Assert.True(await conn.IsGithubAccountVerified(legacy));

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

    [Fact]
    public async Task OnlyAValidUserBoundProofOfABannedIdentityLocksTheAccount()
    {
        var gistUrl = "HTTPS://GIST.GITHUB.COM:443/old-login/abcdef123456?file=proof.txt#" + new string('x', 120);
        var github = new GithubHandler();
        await using var tester = await StartServer(github);
        var linked = await User(tester, 444);
        var target = await User(tester);
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.ExecuteAsync("INSERT INTO github_bans (github_user_id, github_login, reason) VALUES (444, 'old-login', 'probe')");
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
        github.Reply = () => Task.FromResult(Proof(444, "old-login", target));
        using var valid = await Submit(browser, await invalid.Content.ReadAsStringAsync(), gistUrl);
        Assert.Equal(HttpStatusCode.Redirect, valid.StatusCode);
        Assert.Equal("/errors/403", new Uri(browser.BaseAddress!, valid.Headers.Location!).AbsolutePath);
        Assert.Equal(new[] { target, linked }.Order(),
            (await conn.QueryAsync<string>("SELECT user_id FROM admin_account_locks")).Order());
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
    public async Task ANewProofOnlyReplacesAnUnrecoverableLegacyIdentity(bool recoverable)
    {
        var github = new GithubHandler();
        await using var tester = await StartServer(github);
        var legacy = await User(tester);
        await SetIdentity(tester, legacy, null);
        var linked = await User(tester, 444);
        github.Reply = () => Task.FromResult(Proof(444, "renamed-owner", recoverable ? legacy : "wrong-token"));
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.ExecuteAsync("INSERT INTO github_bans (github_user_id, github_login, reason) VALUES (444, 'old-login', 'probe')");
        const string newProof = "https://gist.github.com/clean-owner/fedcba";

        var identities = tester.GetService<GithubIdentityService>();
        if (!recoverable)
            Assert.False(await identities.CanBuild(legacy, false));
        var accepted = await identities.SaveProof(legacy, newProof, new VerifiedGithubIdentity(999, "clean-owner"));

        Assert.Equal(!recoverable, accepted);
        Assert.Equal(recoverable ? 444 : 999, await conn.ExecuteScalarAsync<long?>(
            "SELECT \"GithubUserId\" FROM \"AspNetUsers\" WHERE \"Id\" = @legacy", new { legacy }));
        Assert.Equal(recoverable ? ProofUrl : newProof, await conn.ExecuteScalarAsync<string>(
            "SELECT \"GithubGistUrl\" FROM \"AspNetUsers\" WHERE \"Id\" = @legacy", new { legacy }));
        Assert.Equal((recoverable ? new[] { legacy, linked } : Array.Empty<string>()).Order(),
            (await conn.QueryAsync<string>("SELECT user_id FROM admin_account_locks")).Order());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildingRecoversTheLegacyIdentityBeforeCheckingTheBan(bool banned)
    {
        var github = new GithubHandler();
        await using var tester = await StartServer(github);
        var userId = await User(tester);
        await SetIdentity(tester, userId, null);
        github.Reply = () => Task.FromResult(Proof(777, "renamed-owner", userId));
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        if (banned)
            await conn.ExecuteAsync("INSERT INTO github_bans (github_user_id, github_login, reason) VALUES (777, 'renamed-owner', 'probe')");

        Assert.Equal(!banned, await tester.GetService<GithubIdentityService>().CanBuild(userId, true));
        Assert.Equal(777, await conn.ExecuteScalarAsync<long?>("""SELECT "GithubUserId" FROM "AspNetUsers" WHERE "Id" = @userId""", new { userId }));
        Assert.Equal(banned, await conn.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM admin_account_locks WHERE user_id = @userId)", new { userId }));
    }

    [Fact]
    public async Task BurnIncludesAFirstProofAlreadyBeingSaved()
    {
        await using var tester = await StartServer();
        var (admin, _, _) = await AdminClient(tester);
        using var client = admin;
        var owner = await User(tester, 444);
        var target = await User(tester);
        var connections = tester.GetService<DBConnectionFactory>();
        await using var blocking = await connections.Open();
        await using var observer = await connections.Open();
        await using var tx = await blocking.BeginTransactionAsync();
        await blocking.ExecuteAsync("SELECT 1 FROM \"AspNetUsers\" WHERE \"Id\" = @target FOR UPDATE", new { target }, tx);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var proof = tester.GetService<GithubIdentityService>().SaveProof(target, ProofUrl,
            new VerifiedGithubIdentity(444, "old-login"), timeout.Token);
        Task<HttpResponseMessage>? burn = null;
        try
        {
            int? proofPid;
            while ((proofPid = await observer.QuerySingleOrDefaultAsync<int?>(new CommandDefinition("""
                       SELECT pid FROM pg_stat_activity
                       WHERE datname = current_database() AND @blockingPid = ANY(pg_blocking_pids(pid)) LIMIT 1
                       """, new { blockingPid = blocking.ProcessID }, cancellationToken: timeout.Token))) is null)
                timeout.Token.ThrowIfCancellationRequested();

            burn = client.PostAsJsonAsync($"/api/v1/admin/users/{owner}/burn", new { reason = "concurrent proof" }, timeout.Token);
            // Release the proof only once burn has either finished or is waiting behind that proof.
            // Without proof serialization, burn finishes before the new identity becomes visible.
            while (!burn.IsCompleted && !await observer.ExecuteScalarAsync<bool>(new CommandDefinition("""
                       SELECT EXISTS(SELECT 1 FROM pg_stat_activity
                         WHERE datname = current_database() AND @proofPid = ANY(pg_blocking_pids(pid)))
                       """, new { proofPid }, cancellationToken: timeout.Token)))
                timeout.Token.ThrowIfCancellationRequested();
        }
        finally { await tx.RollbackAsync(); }

        Assert.True(await proof.WaitAsync(TimeSpan.FromSeconds(10)));
        using var response = await burn!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { owner, target }.Order(),
            (await observer.QueryAsync<string>("SELECT user_id FROM admin_account_locks")).Order());
        Assert.Equal(444, await observer.ExecuteScalarAsync<long?>(
            "SELECT \"GithubUserId\" FROM \"AspNetUsers\" WHERE \"Id\" = @target", new { target }));
        Assert.Equal(1, await observer.ExecuteScalarAsync<int>("SELECT count(*) FROM github_bans WHERE github_user_id = 444"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADelayedRecoveryDoesNotOverwriteAProofSavedDuringTheRequest(bool identityKnown)
    {
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var github = new GithubHandler();
        await using var tester = await StartServer(github, singleConnection: true);
        var userId = await User(tester);
        await SetIdentity(tester, userId, null);
        github.Reply = async () => { requested.TrySetResult(); await release.Task; return Proof(111, "stale-owner", userId); };
        var recovery = tester.GetService<GithubIdentityService>().Recover(userId);
        var newProof = identityKnown ? ProofUrl : "https://gist.github.com/new-owner/fedcba";
        try
        {
            await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // The sole pooled connection must be available while the network request waits.
            await SetIdentity(tester, userId, identityKnown ? 222 : null, "new-owner", newProof).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.TrySetResult(); }
        Assert.Equal(identityKnown ? 222L : (long?)null, await recovery.WaitAsync(TimeSpan.FromSeconds(5)));
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        Assert.Equal("new-owner", (await conn.GetAccountDetailSettings(userId))!.Github);
        Assert.Equal(newProof, await conn.ExecuteScalarAsync<string>(
            "SELECT \"GithubGistUrl\" FROM \"AspNetUsers\" WHERE \"Id\" = @userId", new { userId }));
    }

    private async Task<ServerTester> StartServer(GithubHandler? github = null, bool singleConnection = false)
    {
        var tester = Create(nameof(GithubBanTests));
        tester.ReuseDatabase = false;
        tester.ConfigureServices = services =>
        {
            foreach (var service in services.Where(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType != typeof(DatabaseStartupHostedService)).ToArray())
                services.Remove(service);
            services.AddHttpClient(HttpClientNames.GitHub).ConfigurePrimaryHttpMessageHandler(() => github ?? new GithubHandler());
            if (singleConnection)
                services.AddSingleton(sp => new DBConnectionFactory(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["POSTGRES"] = new NpgsqlConnectionStringBuilder(sp.GetRequiredService<IConfiguration>()["POSTGRES"]) { MaxPoolSize = 1, Timeout = 3 }.ToString()
                }).Build()));
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
