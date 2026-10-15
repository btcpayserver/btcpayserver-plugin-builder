using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PluginBuilder.Authentication;
using PluginBuilder.HostedServices;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests;

public class PublisherTokenTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    private const string Password = "test-password:with-colons:123";

    [Fact]
    public async Task ATokenReachesOnlyItsOwnPluginAndOnlyItsHashIsStored()
    {
        await using var tester = await StartServer();
        using var scope = tester.WebApp.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var tokens = scope.ServiceProvider.GetRequiredService<PublisherAccessTokenService>();
        var owner = await AddUser(users, "owner@example.com");
        var other = await AddUser(users, "other@example.com");
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.NewPlugin(new PluginSlug("token-a"), owner.Id);
        await conn.NewPlugin(new PluginSlug("token-b"), owner.Id);
        await conn.NewPlugin(new PluginSlug("token-c"), other.Id);

        var issued = await tokens.Create(owner.Id, "token-a", "CI release", 30);
        Assert.StartsWith(PublisherTokenAuthenticationHandler.Prefix, issued.Token);
        Assert.Equal(AdminTokenAuthenticationHandler.Hash(issued.Token),
            await conn.ExecuteScalarAsync<string>("SELECT token_hash FROM publisher_access_tokens WHERE id = @id", new { id = issued.Id }));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM publisher_access_tokens t WHERE strpos(t::text, @token) > 0", new { token = issued.Token }));

        using var client = tester.CreateHttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issued.Token);
        Assert.Null(await conn.ExecuteScalarAsync<DateTime?>("SELECT last_used_at FROM publisher_access_tokens WHERE id = @id", new { id = issued.Id }));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/plugins/token-a/builds")).StatusCode);
        Assert.NotNull(await conn.ExecuteScalarAsync<DateTime?>("SELECT last_used_at FROM publisher_access_tokens WHERE id = @id", new { id = issued.Id }));
        // Another plugin of the same owner, and someone else's plugin.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/plugins/token-b/builds")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/plugins/token-c/builds")).StatusCode);
        // Not an admin credential, even for routes outside the plugin API.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/admin/me")).StatusCode);
        // Removing the owner ends the token's access, through the ownership check on every request.
        await conn.ExecuteAsync("DELETE FROM users_plugins WHERE user_id = @id AND plugin_slug = 'token-a'", new { id = owner.Id });
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/plugins/token-a/builds")).StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", PublisherTokenAuthenticationHandler.Prefix + new string('0', 64));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/plugins/token-b/builds")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", PublisherTokenAuthenticationHandler.Prefix + "short");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/plugins/token-b/builds")).StatusCode);

        await Assert.ThrowsAsync<ArgumentException>(() => tokens.Create(owner.Id, "token-c", "Not mine", 30));
        await Assert.ThrowsAsync<ArgumentException>(() => tokens.Create(owner.Id, "token-b", "Too long", 366));
    }

    [Fact]
    public async Task RevokedExpiredLockedAndPasswordChangedTokensAreRefused()
    {
        await using var tester = await StartServer();
        using var scope = tester.WebApp.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var tokens = scope.ServiceProvider.GetRequiredService<PublisherAccessTokenService>();
        var owner = await AddUser(users, "owner@example.com");
        var other = await AddUser(users, "other@example.com");
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.NewPlugin(new PluginSlug("refusals"), owner.Id);
        using var client = tester.CreateHttpClient();
        async Task<HttpStatusCode> Call(string token)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return (await client.GetAsync("/api/v1/plugins/refusals/builds")).StatusCode;
        }

        var revoked = await tokens.Create(owner.Id, "refusals", "Revoked", 30);
        Assert.Equal(HttpStatusCode.OK, await Call(revoked.Token));
        // Only the token's owner can revoke it.
        Assert.False(await tokens.Revoke(other.Id, revoked.Id));
        Assert.Equal(HttpStatusCode.OK, await Call(revoked.Token));
        Assert.True(await tokens.Revoke(owner.Id, revoked.Id));
        Assert.Equal(HttpStatusCode.Unauthorized, await Call(revoked.Token));

        var expired = await tokens.Create(owner.Id, "refusals", "Expired", 30);
        await conn.ExecuteAsync("UPDATE publisher_access_tokens SET expires_at = CURRENT_TIMESTAMP - interval '1 minute' WHERE id = @id", new { id = expired.Id });
        Assert.Equal(HttpStatusCode.Unauthorized, await Call(expired.Token));

        var forever = await tokens.Create(owner.Id, "refusals", "No expiry", null);
        Assert.Null(forever.ExpiresAt);
        Assert.Equal(HttpStatusCode.OK, await Call(forever.Token));
        await users.SetLockoutEndDateAsync(owner, DateTimeOffset.UtcNow.AddHours(1));
        Assert.Equal(HttpStatusCode.Unauthorized, await Call(forever.Token));
        await users.SetLockoutEndDateAsync(owner, null);
        Assert.Equal(HttpStatusCode.OK, await Call(forever.Token));

        // A password change rotates the security stamp, which ends every token created before it.
        Assert.True((await users.ChangePasswordAsync(owner, Password, Password + "-new")).Succeeded);
        Assert.Equal(HttpStatusCode.Unauthorized, await Call(forever.Token));
    }

    [Fact]
    public async Task ATwoFactorAccountUsesItsTokenWhileBasicAuthStaysRefused()
    {
        await using var tester = await StartServer();
        using var scope = tester.WebApp.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var tokens = scope.ServiceProvider.GetRequiredService<PublisherAccessTokenService>();
        var owner = await AddUser(users, "owner@example.com");
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.NewPlugin(new PluginSlug("two-factor"), owner.Id);
        using var basic = tester.CreateHttpClient().SetBasicAuth(owner.UserName!, Password);
        using var bearer = tester.CreateHttpClient();
        async Task<HttpStatusCode> Call(string token)
        {
            bearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return (await bearer.GetAsync("/api/v1/plugins/two-factor/builds")).StatusCode;
        }
        var before = await tokens.Create(owner.Id, "two-factor", "Before 2FA", 90);
        Assert.Equal(HttpStatusCode.OK, (await basic.GetAsync("/api/v1/plugins/two-factor/builds")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, await Call(before.Token));

        Assert.True((await users.SetTwoFactorEnabledAsync(owner, true)).Succeeded);
        Assert.Equal(HttpStatusCode.Unauthorized, (await basic.GetAsync("/api/v1/plugins/two-factor/builds")).StatusCode);
        // Turning two-factor on rotates the security stamp: tokens made before it stop, and new ones work.
        Assert.Equal(HttpStatusCode.Unauthorized, await Call(before.Token));
        var issued = await tokens.Create(owner.Id, "two-factor", "CI", 90);
        Assert.Equal(HttpStatusCode.OK, await Call(issued.Token));
    }

    [Fact]
    public async Task TheAccountPageCreatesATokenShowsItOnceAndRevokesIt()
    {
        await using var tester = await StartServer();
        using var scope = tester.WebApp.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var owner = await AddUser(users, "owner@example.com");
        var other = await AddUser(users, "other@example.com");
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.NewPlugin(new PluginSlug("page-mine"), owner.Id);
        await conn.NewPlugin(new PluginSlug("page-theirs"), other.Id);
        using var browser = HttpTestHelpers.CreateBrowser(tester);
        await HttpTestHelpers.LogIn(browser, owner.Email!, Password);

        var page = await Page(browser, "/account/api-tokens");
        Assert.Contains("<option value=\"page-mine\"", page);
        Assert.DoesNotContain("page-theirs", page);

        // Someone else's plugin, posted by hand, is refused and creates nothing.
        var refused = await Create(browser, page, "Not mine", "page-theirs", 30);
        Assert.Contains("You can only create a token for a plugin you own.", refused);
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM publisher_access_tokens"));

        var created = await Create(browser, page, "GitHub Actions", "page-mine", 0);
        var token = Regex.Match(created, "id=\"IssuedToken\"[^>]*value=\"(pb_pub_[0-9a-f]{64})\"").Groups[1].Value;
        Assert.NotEmpty(token);
        Assert.Contains("GitHub Actions", created);

        using var api = tester.CreateHttpClient();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/v1/plugins/page-mine/builds")).StatusCode);

        // Shown once: a fresh load of the page lists the token but never contains it.
        var later = await Page(browser, "/account/api-tokens");
        Assert.Contains("GitHub Actions", later);
        Assert.DoesNotContain(token, later);

        var id = await conn.ExecuteScalarAsync<Guid>("SELECT id FROM publisher_access_tokens WHERE name = 'GitHub Actions'");
        using (var form = new FormUrlEncodedContent(new Dictionary<string, string>
               {
                   ["__RequestVerificationToken"] = HttpTestHelpers.AntiforgeryToken(later)
               }))
        using (var revoke = await browser.PostAsync($"/account/api-tokens/{id}/revoke", form))
            Assert.Equal(HttpStatusCode.Redirect, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync("/api/v1/plugins/page-mine/builds")).StatusCode);
    }

    private static async Task<string> Create(HttpClient browser, string pageWithForm, string name, string plugin, int days)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Creation.Name"] = name,
            ["Creation.PluginSlug"] = plugin,
            ["Creation.ExpiresInDays"] = days.ToString(),
            ["__RequestVerificationToken"] = HttpTestHelpers.AntiforgeryToken(pageWithForm)
        });
        using var response = await browser.PostAsync("/account/api-tokens", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<string> Page(HttpClient browser, string url)
    {
        using var response = await browser.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<ServerTester> StartServer()
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

    private static async Task<IdentityUser> AddUser(UserManager<IdentityUser> users, string email)
    {
        var user = new IdentityUser { UserName = email, Email = email, EmailConfirmed = true };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        return user;
    }
}
