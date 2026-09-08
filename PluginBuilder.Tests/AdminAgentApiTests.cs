using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json.Linq;
using Npgsql;
using PluginBuilder.Authentication;
using PluginBuilder.HostedServices;
using PluginBuilder.Services;
using PluginBuilder.Util;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests;

public class AdminAgentApiTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    [Fact]
    public async Task StoppingRepeatedHostsClosesBothDatabasePools()
    {
        await using var observer = new NpgsqlConnection("Host=127.0.0.1;Port=61932;Username=postgres;Database=postgres;Pooling=false");
        await observer.OpenAsync();
        for (var i = 0; i < 3; i++)
        {
            await using var tester = await StartAdminServer();
            // Keep the database until after the assertion: dropping it terminates
            // sessions and would hide a pool that outlived its host.
            tester.ReuseDatabase = true;
            try
            {
                var database = tester.GetService<DBConnectionFactory>().ConnectionString.Database;
                await using (var connection = await tester.GetService<DBConnectionFactory>().Open())
                    await connection.ExecuteScalarAsync<int>("SELECT 1");
                await using (var connection = await tester.GetService<NpgsqlDataSource>().OpenConnectionAsync())
                    await connection.ExecuteScalarAsync<int>("SELECT 1");
                const string sessions = "SELECT count(*) FROM pg_stat_activity WHERE datname = @database";
                Assert.True(await observer.ExecuteScalarAsync<int>(sessions, new { database }) >= 2);
                await tester.DisposeAsync();
                // PostgreSQL may take a moment to process the closed sockets.
                var remaining = 0;
                for (var attempt = 0; attempt < 50; attempt++)
                {
                    remaining = await observer.ExecuteScalarAsync<int>(sessions, new { database });
                    if (remaining == 0) break;
                    await Task.Delay(100);
                }
                Assert.Equal(0, remaining);
            }
            finally
            {
                tester.ReuseDatabase = false;
            }
        }
    }

    private async Task<ServerTester> StartAdminServer()
    {
        var tester = Create();
        tester.ReuseDatabase = false;
        tester.ConfigureServices = services =>
        {
            foreach (var service in services.Where(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType != typeof(DatabaseStartupHostedService)).ToArray())
                services.Remove(service);
        };
        await tester.Start();
        return tester;
    }

    private static async Task<IdentityUser> AddUser(UserManager<IdentityUser> users, string email, bool admin = false, string password = "test-password:with-colons:123")
    {
        var user = new IdentityUser { UserName = email, Email = email, EmailConfirmed = true };
        Assert.True((await users.CreateAsync(user, password)).Succeeded);
        if (admin)
            Assert.True((await users.AddToRoleAsync(user, Roles.ServerAdmin)).Succeeded);
        return user;
    }

    private static async Task<JObject> IssueToken(HttpClient client, string name = "Codex local review")
    {
        var response = await client.PostAsJsonAsync("/api/v1/admin/access-tokens", new { name, expiresInDays = 30 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JObject.Parse(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AgentCanInspectUsersBuildsAndReviewListingsWithAuditedRevocableToken()
    {
        await using var tester = await StartAdminServer();
        using var scope = tester.WebApp.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var admin = await AddUser(users, "admin@example.com", true);
        var owner = await AddUser(users, "owner@example.com");
        using var ordinary = tester.CreateHttpClient().SetBasicAuth(owner.UserName!, "test-password:with-colons:123");
        foreach (var resource in new[] { "me", "status", "users", "plugins", "listing-requests", "audit", "access-tokens" })
            Assert.Equal(HttpStatusCode.Forbidden, (await ordinary.GetAsync("/api/v1/admin/" + resource)).StatusCode);
        using var issuer = tester.CreateHttpClient().SetBasicAuth(admin.UserName!, "test-password:with-colons:123");
        var issued = await IssueToken(issuer);
        var tokenId = issued["id"]!.ToObject<Guid>();
        var token = issued["token"]!.Value<string>()!;
        Assert.DoesNotContain(token, await issuer.GetStringAsync("/api/v1/admin/access-tokens"));
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        Assert.Equal(AdminTokenAuthenticationHandler.Hash(token), await conn.ExecuteScalarAsync<string>("SELECT token_hash FROM admin_access_tokens WHERE id = @tokenId", new { tokenId }));
        var slug = new PluginSlug("agent-review");
        Assert.True(await conn.NewPlugin(slug, owner.Id));
        var buildId = await conn.NewBuild(slug, new PluginBuildParameters("https://github.com/example/plugin"), triggeredBy: owner.Id);
        await conn.ExecuteAsync("INSERT INTO builds_logs(plugin_slug, build_id, logs) VALUES (@slug, @buildId, 'first line'), (@slug, @buildId, 'last line')", new { slug = slug.ToString(), buildId });
        var requestId = await conn.CreateListingRequest(slug, "Release", "Telegram proof", "User reviews", null, owner.Id);
        using var agent = tester.CreateHttpClient();
        agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var meResponse = await agent.GetAsync("/api/v1/admin/me");
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
        Assert.False(meResponse.Headers.Contains("Set-Cookie"));
        var userJson = await agent.GetStringAsync($"/api/v1/admin/users/{owner.Id}");
        Assert.Contains(owner.Email!, userJson);
        Assert.DoesNotContain("passwordHash", userJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("securityStamp", userJson, StringComparison.OrdinalIgnoreCase);
        var userList = JObject.Parse(await agent.GetStringAsync("/api/v1/admin/users?email=owner%40example.com"));
        Assert.Single((JArray)userList["items"]!);
        var projects = JObject.Parse(await agent.GetStringAsync($"/api/v1/admin/plugins?userId={owner.Id}"));
        Assert.Single((JArray)projects["items"]!);
        Assert.Equal("unlisted", JObject.Parse(await agent.GetStringAsync($"/api/v1/admin/plugins/{slug}"))["visibility"]!.Value<string>());
        var build = JObject.Parse(await agent.GetStringAsync($"/api/v1/admin/plugins/{slug}/builds/{buildId}"));
        Assert.Equal(owner.Id, build["triggeredBy"]!.Value<string>());
        var logPage = JObject.Parse(await agent.GetStringAsync($"/api/v1/admin/plugins/{slug}/builds/{buildId}/logs?limit=1"));
        Assert.True(logPage["hasMore"]!.Value<bool>());
        Assert.Equal("last line", logPage["items"]![0]!["text"]!.Value<string>());
        var olderLogs = JObject.Parse(await agent.GetStringAsync($"/api/v1/admin/plugins/{slug}/builds/{buildId}/logs?before={logPage["nextCursor"]}"));
        Assert.Equal("first line", olderLogs["items"]![0]!["text"]!.Value<string>());
        var listings = JObject.Parse(await agent.GetStringAsync("/api/v1/admin/listing-requests"));
        Assert.Single((JArray)listings["items"]!);
        Assert.Equal(HttpStatusCode.BadRequest, (await agent.GetAsync("/api/v1/admin/listing-requests?status=invalid")).StatusCode);
        var reviewPath = $"/api/v1/admin/listing-requests/{requestId}/review";
        Assert.Equal(HttpStatusCode.BadRequest, (await agent.PostAsJsonAsync(reviewPath, new { decision = "reject", note = " " })).StatusCode);
        var approved = await agent.PostAsJsonAsync(reviewPath, new { decision = "approve", note = "Reviewed metadata and source.", reviewedBy = owner.Id });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var decision = JObject.Parse(await approved.Content.ReadAsStringAsync());
        Assert.Equal(admin.Id, decision["reviewedBy"]!.Value<string>());
        Assert.Equal(tokenId, decision["reviewedByToken"]!.ToObject<Guid>());
        Assert.Equal("listed", await conn.ExecuteScalarAsync<string>("SELECT visibility::text FROM plugins WHERE slug = @slug", new { slug = slug.ToString() }));
        Assert.Equal(HttpStatusCode.Conflict, (await agent.PostAsJsonAsync(reviewPath, new { decision = "reject", note = "Late conflicting review" })).StatusCode);
        var events = JObject.Parse(await agent.GetStringAsync("/api/v1/admin/events?type=listing.approved"));
        Assert.Equal(tokenId, events["events"]![0]!["data"]!["tokenId"]!.ToObject<Guid>());
        var audit = JObject.Parse(await agent.GetStringAsync($"/api/v1/admin/audit?tokenId={tokenId}"));
        Assert.Contains((JArray)audit["items"]!, row => row["path"]!.Value<string>() == reviewPath && row["statusCode"]!.Value<int?>() == 200);
        Assert.Contains((JArray)audit["items"]!, row => row["path"]!.Value<string>() == reviewPath && row["statusCode"]!.Value<int?>() == 409);
        Assert.DoesNotContain(token, audit.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.PostAsJsonAsync("/api/v1/admin/access-tokens", new { name = "Unauthorized replacement" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await issuer.DeleteAsync($"/api/v1/admin/access-tokens/{tokenId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.GetAsync("/api/v1/admin/events")).StatusCode);
        Assert.True(await conn.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM admin_api_audit WHERE token_id = @tokenId)", new { tokenId }));
    }

    [Fact]
    public async Task TokensRespectExpiryRoleRemovalPasswordChangeAndOwnership()
    {
        await using var tester = await StartAdminServer();
        using var scope = tester.WebApp.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var admin = await AddUser(users, "admin@example.com", true);
        var other = await AddUser(users, "other@example.com", true);
        using var issuer = tester.CreateHttpClient().SetBasicAuth(admin.UserName!, "test-password:with-colons:123");
        using var otherIssuer = tester.CreateHttpClient().SetBasicAuth(other.UserName!, "test-password:with-colons:123");
        var issued = await IssueToken(issuer);
        var tokenId = issued["id"]!.ToObject<Guid>();
        using var agent = tester.CreateHttpClient();
        agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issued["token"]!.Value<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await otherIssuer.DeleteAsync($"/api/v1/admin/access-tokens/{tokenId}")).StatusCode);
        Assert.Empty(JArray.Parse(await otherIssuer.GetStringAsync("/api/v1/admin/access-tokens")));
        await users.RemoveFromRoleAsync(admin, Roles.ServerAdmin);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync("/api/v1/admin/me")).StatusCode);
        await users.AddToRoleAsync(admin, Roles.ServerAdmin);
        Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync("/api/v1/admin/me")).StatusCode);
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.ExecuteAsync("UPDATE admin_access_tokens SET expires_at = CURRENT_TIMESTAMP - INTERVAL '1 second' WHERE id = @tokenId", new { tokenId });
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.GetAsync("/api/v1/admin/me")).StatusCode);
        var second = await IssueToken(issuer, "Second token");
        agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", second["token"]!.Value<string>());
        await users.UpdateSecurityStampAsync(admin);
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.GetAsync("/api/v1/admin/me")).StatusCode);
    }

    [Fact]
    public async Task BasicAuthRejectsMalformedHeadersAndDoesNotCreateBrowserSessions()
    {
        await using var tester = await StartAdminServer();
        using var scope = tester.WebApp.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var admin = await AddUser(users, "admin@example.com", true);
        using var client = tester.CreateHttpClient();
        foreach (var value in new[] { "!invalid!", Convert.ToBase64String(Encoding.UTF8.GetBytes("no-colon")), "Og==", "/w==" })
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", value);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/admin/me")).StatusCode);
        }
        client.SetBasicAuth(admin.UserName!, "test-password:with-colons:123");
        var response = await client.GetAsync("/api/v1/admin/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        await users.SetLockoutEndDateAsync(admin, DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/admin/me")).StatusCode);
    }

    [Fact]
    public async Task BrowserCanCreateAndRevokeTokensWithAntiforgeryProtection()
    {
        await using var tester = await StartAdminServer();
        using var scope = tester.WebApp.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var admin = await AddUser(users, "admin@example.com", true);
        using var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer() })
        {
            BaseAddress = new Uri(tester.WebApp.Urls.First())
        };
        static string Csrf(string html) => WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        var loginHtml = await browser.GetStringAsync("/login");
        var login = await browser.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = admin.Email!, ["Password"] = "test-password:with-colons:123", ["__RequestVerificationToken"] = Csrf(loginHtml)
        }));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        const string path = "/account/admin-access-tokens";
        var page = await browser.GetStringAsync(path);
        var form = new Dictionary<string, string> { ["Creation.Name"] = "Browser agent", ["Creation.ExpiresInDays"] = "7" };
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsync(path, new FormUrlEncodedContent(form))).StatusCode);
        form["__RequestVerificationToken"] = Csrf(page);
        var create = await browser.PostAsync(path, new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var html = await create.Content.ReadAsStringAsync();
        var token = Regex.Match(html, "pb_admin_[a-f0-9]{64}").Value;
        Assert.NotEmpty(token);
        Assert.DoesNotContain(token, await browser.GetStringAsync(path));
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        var id = await conn.ExecuteScalarAsync<Guid>("SELECT id FROM admin_access_tokens WHERE name = 'Browser agent'");
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsync($"{path}/{id}/revoke", new FormUrlEncodedContent(new Dictionary<string, string>()))).StatusCode);
        var revoke = await browser.PostAsync($"{path}/{id}/revoke", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = Csrf(html) }));
        Assert.Equal(HttpStatusCode.Redirect, revoke.StatusCode);
        Assert.True(await conn.ExecuteScalarAsync<bool>("SELECT revoked_at IS NOT NULL FROM admin_access_tokens WHERE id = @id", new { id }));
    }

    [Fact]
    public async Task CompetingReviewersCannotOverwriteEachOther()
    {
        await using var tester = await StartAdminServer();
        using var scope = tester.WebApp.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var admin = await AddUser(users, "admin@example.com", true);
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        var slug = new PluginSlug("review-race");
        await conn.NewPlugin(slug, admin.Id);
        var id = await conn.CreateListingRequest(slug, "Release", "Proof", "Reviews", null, admin.Id);
        var service = scope.ServiceProvider.GetRequiredService<ListingReviewService>();
        var outcomes = await Task.WhenAll(service.Review(id, admin.Id, true, "Approved", _ => null),
            service.Review(id, admin.Id, false, "Rejected", _ => null));
        Assert.Single(outcomes, o => o == ListingReviewService.Outcome.Completed);
        Assert.Single(outcomes, o => o == ListingReviewService.Outcome.AlreadyProcessed);
        var approved = await conn.ExecuteScalarAsync<bool>("SELECT status = 'approved' FROM plugin_listing_requests WHERE id = @id", new { id });
        Assert.Equal(approved, await conn.ExecuteScalarAsync<bool>("SELECT visibility = 'listed' FROM plugins WHERE slug = @slug", new { slug = slug.ToString() }));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_events WHERE type IN ('listing.approved', 'listing.rejected')"));
    }
}
