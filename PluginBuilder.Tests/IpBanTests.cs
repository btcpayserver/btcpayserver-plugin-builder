using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json.Linq;
using Npgsql;
using PluginBuilder.HostedServices;
using PluginBuilder.Services;
using PluginBuilder.Util;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class IpBanTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    private const string Password = "test-password:123";
    private const string Refusal = "Please provide a valid email address.";

    [Theory]
    [InlineData("203.0.113.99, ::ffff:198.51.100.31", "198.51.100.31", "198.51.100.31/32", "198.51.100.32")]
    [InlineData("203.0.113.99, 2001:db8:1::31", "2001:db8:1::31", "2001:db8:1::31/128", "2001:db8:1::32")]
    public async Task RegistrationCapturesTheSignupHostAndAnExactBanCanBeRevoked(
        string forwardedIp, string storedIp, string network, string otherIp)
    {
        await using var tester = await StartServer();
        var (admin, _, _) = await AdminClient(tester);
        using var client = admin;
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        var first = Email();
        Assert.Equal(HttpStatusCode.Redirect, (await Register(tester, first, forwardedIp)).Status);
        Assert.Equal(storedIp, await conn.QuerySingleAsync<string>(
            """SELECT host("SignupIp") FROM "AspNetUsers" WHERE "Email" = @first""", new { first }));

        var ban = await Ban(client, network);
        var refused = Email();
        var response = await Register(tester, refused, storedIp);
        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Contains(Refusal, response.Body);
        Assert.False(await UserExists(conn, refused));
        Assert.Equal(HttpStatusCode.Redirect, (await Register(tester, Email(), otherIp)).Status);

        using var revoked = await client.PostAsJsonAsync($"/api/v1/admin/ip-bans/{ban.Value<long>("id")}/revoke",
            new { reason = "reviewed" });
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.True(JObject.Parse(await revoked.Content.ReadAsStringAsync()).Value<bool>("revoked"));
        Assert.Equal(HttpStatusCode.Redirect, (await Register(tester, refused, forwardedIp)).Status);
        Assert.Equal(storedIp, await conn.QuerySingleAsync<string>(
            """SELECT host("SignupIp") FROM "AspNetUsers" WHERE "Email" = @refused""", new { refused }));
    }

    [Fact]
    public async Task SignupWithoutForwardedHeadersUsesTheSocketAddress()
    {
        await using var tester = await StartServer();
        var (admin, _, _) = await AdminClient(tester);
        using var client = admin;
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        var first = Email();
        Assert.Equal(HttpStatusCode.Redirect,
            (await Register(tester, first, null)).Status);
        var socket = await conn.QuerySingleAsync<string>(
            """SELECT host("SignupIp") FROM "AspNetUsers" WHERE "Email" = @first""", new { first });
        Assert.True(IPAddress.IsLoopback(IPAddress.Parse(socket)));
        await Ban(client, socket);
        var refused = Email();
        var response = await Register(tester, refused, null);
        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Contains(Refusal, response.Body);
        Assert.False(await UserExists(conn, refused));
    }

    [Fact]
    public async Task AdminApiValidatesBansPreservesDuplicateMetadataAndAuditsTheActor()
    {
        await using var tester = await StartServer();
        var (admin, actor, tokenId) = await AdminClient(tester);
        using var client = admin;
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        using var anonymous = tester.CreateHttpClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/admin/ip-bans")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/admin/ip-bans",
            new { network = "198.51.100.40", reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/admin/ip-bans/1/revoke",
            new { reason = "x" })).StatusCode);
        var owner = await tester.CreateFakeUserAsync(password: Password);
        var ownerEmail = await conn.QuerySingleAsync<string>(
            """SELECT "Email" FROM "AspNetUsers" WHERE "Id" = @owner""", new { owner });
        using var ordinary = tester.CreateHttpClient().SetBasicAuth(ownerEmail, Password);
        Assert.Equal(HttpStatusCode.Forbidden, (await ordinary.GetAsync("/api/v1/admin/ip-bans")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ordinary.PostAsJsonAsync("/api/v1/admin/ip-bans",
            new { network = "198.51.100.40", reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ordinary.PostAsJsonAsync("/api/v1/admin/ip-bans/1/revoke",
            new { reason = "x" })).StatusCode);

        foreach (var network in new[] { "invalid", "127.1", "2130706433", "198.51.100.0/24", "2001:db8::/48", "2001:db8::/129" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/admin/ip-bans",
                new { network, reason = "x" })).StatusCode);
        foreach (var reason in new[] { " ", new string('x', 1001) })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/admin/ip-bans",
                new { network = "198.51.100.40", reason })).StatusCode);
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM ip_bans"));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_events WHERE type LIKE 'ip.%'"));

        var ban = await Ban(client, "::ffff:198.51.100.40/128", " original reason ");
        var id = ban.Value<long>("id");
        Assert.Equal("198.51.100.40/32", ban.Value<string>("network"));
        var original = Assert.Single(JArray.Parse(await client.GetStringAsync("/api/v1/admin/ip-bans")));
        Assert.Equal(id, original.Value<long>("id"));
        Assert.Equal("198.51.100.40/32", original.Value<string>("network"));
        Assert.Equal("original reason", original.Value<string>("reason"));
        Assert.Equal(actor, original.Value<string>("bannedBy"));
        Assert.NotNull(original["bannedAt"]);
        var duplicate = await Ban(client, "198.51.100.40/32", "replacement reason");
        Assert.Equal(id, duplicate.Value<long>("id"));
        Assert.True(JToken.DeepEquals(original, Assert.Single(JArray.Parse(await client.GetStringAsync("/api/v1/admin/ip-bans")))));
        var created = JObject.Parse(await conn.QuerySingleAsync<string>(
            "SELECT data::text FROM admin_events WHERE type = 'ip.banned'"));
        Assert.Equal("198.51.100.40/32", created.Value<string>("network"));
        Assert.Equal("original reason", created.Value<string>("reason"));
        Assert.Equal(actor, created.Value<string>("by"));
        Assert.Equal(tokenId, created.Value<string>("tokenId"));

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/v1/admin/ip-bans/{id}/revoke",
            new { reason = " " })).StatusCode);
        var adminEmail = await conn.QuerySingleAsync<string>(
            """SELECT "Email" FROM "AspNetUsers" WHERE "Id" = @actor""", new { actor });
        using var basic = tester.CreateHttpClient().SetBasicAuth(adminEmail, Password);
        Assert.Equal(HttpStatusCode.OK, (await basic.PostAsJsonAsync($"/api/v1/admin/ip-bans/{id}/revoke",
            new { reason = "false positive" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/v1/admin/ip-bans/{id}/revoke",
            new { reason = "again" })).StatusCode);
        var revoked = JObject.Parse(await conn.QuerySingleAsync<string>(
            "SELECT data::text FROM admin_events WHERE type = 'ip.ban_revoked'"));
        Assert.Equal(id, revoked.Value<long>("banId"));
        Assert.Equal("false positive", revoked.Value<string>("reason"));
        Assert.Equal(actor, revoked.Value<string>("by"));
        Assert.Equal(JTokenType.Null, revoked["tokenId"]!.Type);
        Assert.Empty(JArray.Parse(await client.GetStringAsync("/api/v1/admin/ip-bans")));
    }

    [Fact]
    public async Task AManualIpv6SubnetBanCoversIts64BitsAndLeavesTheNextSubnetOpen()
    {
        await using var tester = await StartServer();
        var (admin, _, _) = await AdminClient(tester);
        using var client = admin;
        var ban = await Ban(client, "2001:db8:2:3::abcd/64");
        Assert.Equal("2001:db8:2:3::/64", ban.Value<string>("network"));
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        var refused = Email();
        var response = await Register(tester, refused, "2001:db8:2:3:ffff:ffff:ffff:ffff");
        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Contains(Refusal, response.Body);
        Assert.False(await UserExists(conn, refused));
        Assert.Equal(HttpStatusCode.Redirect, (await Register(tester, Email(), "2001:db8:2:4::1")).Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(null)]
    public async Task BurnUsesOnlyFreshSignupEvidenceAndDoesNotLockOtherAccountsSharingTheIp(int? ageDays)
    {
        await using var tester = await StartServer();
        var (admin, actor, _) = await AdminClient(tester);
        using var client = admin;
        var target = await tester.CreateFakeUserAsync(password: Password, githubVerified: false);
        var shared = await tester.CreateFakeUserAsync(password: Password, githubVerified: false);
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        const string ip = "2001:db8:3::1";
        await conn.ExecuteAsync("""
            UPDATE "AspNetUsers" SET "SignupIp" = CAST(@ip AS inet) WHERE "Id" = @shared;
            UPDATE "AspNetUsers" SET "SignupIp" = CAST(@sample AS inet), "CreatedAt" = @created,
              "GithubUserId" = 714, "GithubGistUrl" = 'https://gist.github.com/probe/abcdef123456',
              "AccountDetail" = '{"github":"probe"}'::jsonb WHERE "Id" = @target
            """, new { ip, shared, sample = ageDays is null ? null : ip,
            created = DateTimeOffset.UtcNow.AddDays(-ageDays.GetValueOrDefault()), target });
        var details = JObject.Parse(await client.GetStringAsync($"/api/v1/admin/users/{target}"));
        Assert.Equal(ageDays == 0 ? ip : null, details.Value<string>("signupIp"));
        using var burned = await client.PostAsJsonAsync($"/api/v1/admin/users/{target}/burn", new { reason = "confirmed abuse" });
        Assert.Equal(HttpStatusCode.OK, burned.StatusCode);
        var result = JObject.Parse(await burned.Content.ReadAsStringAsync());
        Assert.Equal(target, Assert.Single(result["lockedUserIds"]!.Values<string>()));
        Assert.Equal(new[] { target }, await conn.QueryAsync<string>("SELECT user_id FROM admin_account_locks"));
        Assert.Equal(actor, await conn.QuerySingleAsync<string>("SELECT banned_by FROM github_bans"));
        if (ageDays == 0)
        {
            Assert.Equal(ip + "/128", result.Value<string>("ipNetwork"));
            Assert.Empty(result["warnings"]!);
            Assert.Equal(ip + "/128", await conn.QuerySingleAsync<string>("SELECT network::text FROM ip_bans"));
            var refused = await Register(tester, Email(), ip);
            Assert.Equal(HttpStatusCode.OK, refused.Status);
            Assert.Contains(Refusal, refused.Body);
            Assert.Equal(HttpStatusCode.Redirect, (await Register(tester, Email(), "2001:db8:3::2")).Status);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/v1/admin/users/{target}/burn",
                new { reason = "repeat" })).StatusCode);
            Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM ip_bans"));
            Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_events WHERE type = 'ip.banned'"));
        }
        else
        {
            Assert.Null(result.Value<string>("ipNetwork"));
            Assert.Contains(result["warnings"]!.Values<string>(), warning => warning!.Contains("IP", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM ip_bans"));
        }
    }

    [Fact]
    public async Task BurnIgnoresSignupEvidenceThatExpiresWhileWaitingForModeration()
    {
        await using var tester = await StartServer();
        var (admin, _, _) = await AdminClient(tester);
        using var client = admin;
        var target = await tester.CreateFakeUserAsync(password: Password);
        await using var blocker = await tester.GetService<DBConnectionFactory>().Open();
        await using var observer = await tester.GetService<DBConnectionFactory>().Open();
        await observer.ExecuteAsync("""
            UPDATE "AspNetUsers" SET "SignupIp" = '198.51.100.47'::inet WHERE "Id" = @target
            """, new { target });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var tx = await blocker.BeginTransactionAsync(timeout.Token);
        await AccountModerationService.Serialize(blocker, tx, timeout.Token);
        var burn = client.PostAsJsonAsync($"/api/v1/admin/users/{target}/burn", new { reason = "confirmed abuse" }, timeout.Token);
        var committed = false;
        try
        {
            DateTime? started;
            while ((started = await observer.QuerySingleOrDefaultAsync<DateTime?>(new CommandDefinition("""
                       SELECT xact_start FROM pg_stat_activity
                       WHERE datname = current_database() AND @pid = ANY(pg_blocking_pids(pid))
                       """, new { pid = blocker.ProcessID }, cancellationToken: timeout.Token))) is null)
            {
                Assert.False(burn.IsCompleted, "Burn must wait for the moderation transaction.");
                await Task.Delay(10, timeout.Token);
            }
            // Place expiry inside the observed wait without relying on a timed sleep.
            Assert.True(await observer.ExecuteScalarAsync<bool>(new CommandDefinition("""
                UPDATE "AspNetUsers" SET "CreatedAt" = @started - INTERVAL '7 days' + INTERVAL '1 microsecond'
                WHERE "Id" = @target
                RETURNING "CreatedAt" > @started - INTERVAL '7 days'
                  AND "CreatedAt" <= clock_timestamp() - INTERVAL '7 days'
                """, new { started, target }, cancellationToken: timeout.Token)));
            await tx.CommitAsync(timeout.Token);
            committed = true;
            using var response = await burn.WaitAsync(timeout.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = JObject.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            Assert.Null(result.Value<string>("ipNetwork"));
            Assert.Contains(result["warnings"]!.Values<string>(), warning => warning!.Contains("No signup IP"));
            Assert.Equal(0, await observer.ExecuteScalarAsync<int>("SELECT count(*) FROM ip_bans"));
            Assert.Equal(0, await observer.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_events WHERE type = 'ip.banned'"));
            Assert.Equal(target, await observer.QuerySingleAsync<string>("SELECT user_id FROM admin_account_locks"));
            Assert.Equal(1, await observer.ExecuteScalarAsync<int>("SELECT count(*) FROM github_bans"));
        }
        finally
        {
            if (!committed) await tx.RollbackAsync();
            if (!burn.IsCompleted)
            {
                timeout.Cancel();
                try { (await burn).Dispose(); }
                catch (OperationCanceledException) { }
            }
        }
    }

    [Fact]
    public async Task RetentionClearsOldSignupSamplesAndKeepsRecentSamplesAndActiveBans()
    {
        await using var tester = await StartServer();
        var (admin, _, _) = await AdminClient(tester);
        using var client = admin;
        var stale = await tester.CreateFakeUserAsync(password: Password);
        var recent = await tester.CreateFakeUserAsync(password: Password);
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.ExecuteAsync("""
            UPDATE "AspNetUsers" SET "SignupIp" = '198.51.100.60'::inet,
              "CreatedAt" = CURRENT_TIMESTAMP - INTERVAL '8 days' WHERE "Id" = @stale;
            UPDATE "AspNetUsers" SET "SignupIp" = '198.51.100.61'::inet,
              "CreatedAt" = CURRENT_TIMESTAMP - INTERVAL '6 days' WHERE "Id" = @recent
            """, new { stale, recent });
        var ban = await Ban(client, "198.51.100.60");
        await tester.GetService<AdminHistoryRetention>().Purge(CancellationToken.None);
        Assert.True(await conn.ExecuteScalarAsync<bool>(
            """SELECT "SignupIp" IS NULL FROM "AspNetUsers" WHERE "Id" = @stale""", new { stale }));
        Assert.Equal("198.51.100.61", await conn.QuerySingleAsync<string>(
            """SELECT host("SignupIp") FROM "AspNetUsers" WHERE "Id" = @recent""", new { recent }));
        Assert.Equal(ban.Value<long>("id"), await conn.QuerySingleAsync<long>("SELECT id FROM ip_bans"));
        var refused = await Register(tester, Email(), "198.51.100.60");
        Assert.Equal(HttpStatusCode.OK, refused.Status);
        Assert.Contains(Refusal, refused.Body);
    }

    [Fact]
    public async Task RegistrationDoesNotWaitForModerationAndRejectsACommittedBan()
    {
        await using var tester = await StartServer();
        await using var blocker = await tester.GetService<DBConnectionFactory>().Open();
        await using var observer = await tester.GetService<DBConnectionFactory>().Open();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var tx = await blocker.BeginTransactionAsync(timeout.Token);
        await AccountModerationService.Serialize(blocker, tx, timeout.Token);
        await blocker.ExecuteAsync(new CommandDefinition("""
            INSERT INTO ip_bans(network, reason) VALUES ('198.51.100.70/32', 'concurrent ban')
            """, transaction: tx, cancellationToken: timeout.Token));

        var first = Email();
        Assert.Equal(HttpStatusCode.Redirect, (await Register(tester, first, "198.51.100.70", timeout.Token)).Status);
        Assert.True(await UserExists(observer, first));
        await tx.CommitAsync(timeout.Token);

        var refused = Email();
        var response = await Register(tester, refused, "198.51.100.70", timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Contains(Refusal, response.Body);
        Assert.False(await UserExists(observer, refused));
    }

    [Fact]
    public async Task ASignupIpWriteFailureRollsBackTheUserAndRegistrationEvent()
    {
        await using var tester = await StartServer();
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        var before = await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM admin_events WHERE type = 'user.registered'");
        await conn.ExecuteAsync("""
            CREATE FUNCTION reject_signup_ip() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'signup IP write rejected by test'; END $$;
            CREATE TRIGGER reject_signup_ip BEFORE UPDATE OF "SignupIp" ON "AspNetUsers"
              FOR EACH ROW EXECUTE FUNCTION reject_signup_ip()
            """);
        var email = Email();
        Assert.Equal(HttpStatusCode.InternalServerError, (await Register(tester, email, "198.51.100.80")).Status);
        Assert.False(await UserExists(conn, email));
        Assert.Equal(before, await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM admin_events WHERE type = 'user.registered'"));
    }

    private async Task<ServerTester> StartServer()
    {
        var tester = Create(nameof(IpBanTests));
        tester.ReuseDatabase = false;
        tester.ConfigureServices = services =>
        {
            foreach (var service in services.Where(s => s.ServiceType == typeof(IHostedService) &&
                         s.ImplementationType != typeof(DatabaseStartupHostedService)).ToArray())
                services.Remove(service);
        };
        await tester.Start();
        return tester;
    }

    private static async Task<(HttpClient Client, string UserId, string TokenId)> AdminClient(ServerTester tester)
    {
        var id = await tester.CreateFakeUserAsync(password: Password);
        using var scope = tester.WebApp.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = (await users.FindByIdAsync(id))!;
        Assert.True((await users.AddToRoleAsync(user, Roles.ServerAdmin)).Succeeded);
        using var issuer = tester.CreateHttpClient().SetBasicAuth(user.Email!, Password);
        using var response = await issuer.PostAsJsonAsync("/api/v1/admin/access-tokens", new { name = "IP ban test", expiresInDays = 1 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var token = JObject.Parse(await response.Content.ReadAsStringAsync());
        var client = tester.CreateHttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value<string>("token"));
        return (client, id, token.Value<string>("id")!);
    }

    private static async Task<JObject> Ban(HttpClient client, string network, string reason = "confirmed abuse")
    {
        using var response = await client.PostAsJsonAsync("/api/v1/admin/ip-bans", new { network, reason });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JObject.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<(HttpStatusCode Status, string Body)> Register(ServerTester tester, string email,
        string? ip, CancellationToken cancellationToken = default)
    {
        using var client = HttpTestHelpers.CreateBrowser(tester);
        if (ip is not null)
            client.DefaultRequestHeaders.Add("X-Forwarded-For", ip);
        var page = await client.GetStringAsync("/register", cancellationToken);
        using var response = await client.PostAsync("/register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = Password,
            ["ConfirmPassword"] = Password,
            ["__RequestVerificationToken"] = HttpTestHelpers.AntiforgeryToken(page)
        }), cancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static Task<bool> UserExists(NpgsqlConnection conn, string email) => conn.ExecuteScalarAsync<bool>(
        """SELECT EXISTS(SELECT 1 FROM "AspNetUsers" WHERE "Email" = @email)""", new { email });

    private static string Email() => $"ip-ban-{Guid.NewGuid():N}@example.org";
}
