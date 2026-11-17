using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
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
public class AdditionalBlockedEmailDomainsTests(ITestOutputHelper output) : UnitTestBase(output)
{
    private const string Password = "test-password:123";
    private const string PagePath = "/admin/moderation";
    private const string CreatePath = PagePath + "/bans";
    private const string Refusal = "Please provide a valid email address.";

    [Theory]
    [InlineData("email_domain.banned")]
    [InlineData("email_domain.ban_revoked")]
    public async Task EmailDomainBanEventsCanBeFilteredAndSubscribedTo(string eventType)
    {
        await using var tester = await StartServer();
        var (browser, actor) = await AdminBrowser(tester);
        using var admin = browser;
        using var scope = tester.WebApp.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = (await users.FindByIdAsync(actor))!;
        using var issuer = tester.CreateHttpClient().SetBasicAuth(user.Email!, Password);
        using var issued = await issuer.PostAsJsonAsync("/api/v1/admin/access-tokens",
            new { name = "domain event test", expiresInDays = 1 });
        Assert.Equal(HttpStatusCode.Created, issued.StatusCode);
        var token = JObject.Parse(await issued.Content.ReadAsStringAsync()).Value<string>("token");
        using var api = tester.CreateHttpClient();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        const string eventsPath = "/api/v1/admin/events";
        var types = JArray.Parse(await api.GetStringAsync(eventsPath + "/types")).Values<string>();
        Assert.Contains(eventType, types);
        using var subscribed = await api.PostAsJsonAsync(eventsPath + "/subscriptions", new
        {
            kind = "email", destination = "admin@example.test", eventTypes = new[] { eventType }
        });
        Assert.Equal(HttpStatusCode.Created, subscribed.StatusCode);
        var subscriptionId = JObject.Parse(await subscribed.Content.ReadAsStringAsync()).Value<string>("id");

        var banId = await Ban(admin, "events.example");
        await Revoke(admin, banId);

        var page = JObject.Parse(await api.GetStringAsync(eventsPath + "?type=" + eventType));
        var entry = Assert.IsType<JObject>(Assert.Single((JArray)page["events"]!));
        Assert.Equal(eventType, entry.Value<string>("type"));
        Assert.Equal(banId, entry["data"]!.Value<long>("banId"));
        Assert.Equal("events.example", entry["data"]!.Value<string>("domain"));
        Assert.Equal(actor, entry["data"]!.Value<string>("by"));
        var deliveries = JArray.Parse(await api.GetStringAsync(eventsPath + $"/subscriptions/{subscriptionId}/deliveries"));
        var delivery = Assert.IsType<JObject>(Assert.Single(deliveries));
        Assert.Equal(entry.Value<string>("id"), delivery.Value<string>("eventId"));
        Assert.Equal("pending", delivery.Value<string>("status"));
    }

    [Fact]
    public async Task DomainBansPreserveMetadataAndApplyOnlyToNewRegistrations()
    {
        await using var tester = await StartServer();
        var (admin, actor) = await AdminBrowser(tester);
        using var client = admin;
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        const string existingEmail = "existing@throwaway.example";
        await tester.CreateFakeUserAsync(existingEmail, Password, githubVerified: false);

        var id = await Ban(client, " Throwaway.Example. ", " original evidence ");
        var snapshot = await Snapshot(conn, id);
        var metadata = JObject.Parse(snapshot);
        Assert.Equal("throwaway.example", metadata.Value<string>("domain"));
        Assert.Equal("original evidence", metadata.Value<string>("reason"));
        Assert.Equal(actor, metadata.Value<string>("banned_by"));
        Assert.False(string.IsNullOrEmpty(metadata.Value<string>("banned_at")));
        Assert.Equal(id, await Ban(client, "THROWAWAY.EXAMPLE", "replacement evidence"));
        Assert.Equal(snapshot, await Snapshot(conn, id));
        var created = JObject.Parse(await conn.QuerySingleAsync<string>("""
            SELECT data::text FROM admin_events WHERE type = 'email_domain.banned' AND data->>'domain' = 'throwaway.example'
            """));
        Assert.Equal(id, created.Value<long>("banId"));
        Assert.Equal(actor, created.Value<string>("by"));
        Assert.Equal("original evidence", created.Value<string>("reason"));
        Assert.Null(created.Value<string>("tokenId"));

        var internationalId = await Ban(client, "BÜCHER.example");
        Assert.Equal("xn--bcher-kva.example", JObject.Parse(await Snapshot(conn, internationalId)).Value<string>("domain"));
        using var anotherAdmin = (await AdminBrowser(tester)).Browser;
        Assert.Equal(new[] { ("email-domain", id), ("email-domain", internationalId) }.OrderBy(row => row.Item2),
            RowsOnPage(await anotherAdmin.GetStringAsync(PagePath + "?kind=email-domain")).OrderBy(row => row.Id));

        foreach (var domain in new[] { "throwaway.example", "inbox.THROWAWAY.EXAMPLE", "throwaway\u3002example", "bücher.example" })
        {
            var email = Email(domain);
            var refused = await Register(tester, email);
            Assert.Equal(HttpStatusCode.OK, refused.Status);
            Assert.Contains(Refusal, refused.Body);
            Assert.False(await UserExists(conn, email));
        }
        var unrelated = Email("notthrowaway.example");
        Assert.Equal(HttpStatusCode.Redirect, (await Register(tester, unrelated)).Status);
        Assert.True(await UserExists(conn, unrelated));
        using var existing = HttpTestHelpers.CreateBrowser(tester);
        await HttpTestHelpers.LogIn(existing, existingEmail, Password);
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_account_locks"));

        await Revoke(client, id, " reviewed evidence ");
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM email_domain_bans WHERE id = @id", new { id }));
        var revoked = JObject.Parse(await conn.QuerySingleAsync<string>("""
            SELECT data::text FROM admin_events WHERE type = 'email_domain.ban_revoked' AND data->>'domain' = 'throwaway.example'
            """));
        Assert.Equal(id, revoked.Value<long>("banId"));
        Assert.Equal(actor, revoked.Value<string>("by"));
        Assert.Equal("reviewed evidence", revoked.Value<string>("reason"));
        var allowedAfterRevoke = Email("inbox.throwaway.example");
        Assert.Equal(HttpStatusCode.Redirect, (await Register(tester, allowedAfterRevoke)).Status);
        Assert.True(await UserExists(conn, allowedAfterRevoke));

        // Revoking an additional ban cannot remove the built-in registration protection.
        var defaultId = await Ban(client, "mailinator.com");
        await Revoke(client, defaultId);
        var standard = Email("mailinator.com");
        var stillRefused = await Register(tester, standard);
        Assert.Equal(HttpStatusCode.OK, stillRefused.Status);
        Assert.Contains(Refusal, stillRefused.Body);
        Assert.False(await UserExists(conn, standard));
    }

    [Fact]
    public async Task InvalidTargetsAndReasonsDoNotChangeSavedBansOrEmitEvents()
    {
        await using var tester = await StartServer();
        using var admin = (await AdminBrowser(tester)).Browser;
        var id = await Ban(admin, "saved.example");
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        var snapshot = await Snapshot(conn, id);
        foreach (var target in new[] { "", "user@invalid.example", "https://invalid.example", "*.invalid.example",
                     "invalid..example", "first.example\nsecond.example", new string('x', 254) + ".example" })
        {
            using var response = await Post(admin, CreatePath, "email-domain", target, "evidence");
            await AssertValidationProblem(response);
        }
        foreach (var reason in new[] { "", " \t\r\n", new string('x', 1001) })
        {
            using (var response = await Post(admin, CreatePath, "email-domain", "other.example", reason))
                await AssertValidationProblem(response);
            using (var response = await Post(admin, RevokePath(id), null, null, reason))
                await AssertValidationProblem(response);
        }
        using (var response = await Post(admin, CreatePath, "unrecognized", "other.example", "evidence"))
            await AssertValidationProblem(response);
        Assert.Equal(snapshot, await Snapshot(conn, id));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM email_domain_bans"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_events WHERE type = 'email_domain.banned'"));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_events WHERE type = 'email_domain.ban_revoked'"));
    }

    [Fact]
    public async Task ConcurrentCreatesPreserveBothDomainsAndEmitOneEventForEachBan()
    {
        await using var tester = await StartServer();
        using var firstAdmin = (await AdminBrowser(tester)).Browser;
        using var secondAdmin = (await AdminBrowser(tester)).Browser;
        var firstIds = await Task.WhenAll(Ban(firstAdmin, "first.example"), Ban(secondAdmin, "second.example"));
        Assert.NotEqual(firstIds[0], firstIds[1]);
        var duplicateIds = await Task.WhenAll(Ban(firstAdmin, "Shared.Example."), Ban(secondAdmin, "shared.example"));
        Assert.Equal(duplicateIds[0], duplicateIds[1]);
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        Assert.Equal(new[] { "first.example", "second.example", "shared.example" },
            (await conn.QueryAsync<string>("SELECT domain FROM email_domain_bans ORDER BY domain")).ToArray());
        Assert.Equal(3, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_events WHERE type = 'email_domain.banned'"));
        Assert.Equal(3, await conn.ExecuteScalarAsync<int>("""
            SELECT count(*) FROM email_domain_bans b JOIN admin_events e
              ON e.type = 'email_domain.banned' AND (e.data->>'banId')::bigint = b.id AND e.data->>'domain' = b.domain
            """));
    }

    [Fact]
    public async Task CreationAndRevocationRequireAnAdminAndAntiforgeryToken()
    {
        await using var tester = await StartServer();
        using var admin = (await AdminBrowser(tester)).Browser;
        var id = await Ban(admin, "protected.example");
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        using var anonymous = HttpTestHelpers.CreateBrowser(tester);
        using (var page = await anonymous.GetAsync(PagePath))
        {
            Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
            Assert.Contains("/login", page.Headers.Location?.OriginalString ?? "");
        }
        foreach (var path in new[] { CreatePath, RevokePath(id) })
        {
            using var form = Form("email-domain", "other.example", "evidence");
            using var denied = await anonymous.PostAsync(path, form);
            Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
            Assert.Contains("/login", denied.Headers.Location?.OriginalString ?? "");
        }
        const string ordinaryEmail = "ordinary@example.com";
        await tester.CreateFakeUserAsync(ordinaryEmail, Password, githubVerified: false);
        using var ordinary = HttpTestHelpers.CreateBrowser(tester);
        await HttpTestHelpers.LogIn(ordinary, ordinaryEmail, Password);
        var token = HttpTestHelpers.AntiforgeryToken(await ordinary.GetStringAsync("/account/details"));
        using (var page = await ordinary.GetAsync(PagePath))
        {
            Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
            Assert.Contains("/errors/403", page.Headers.Location?.OriginalString ?? "");
        }
        foreach (var path in new[] { CreatePath, RevokePath(id) })
        {
            using var form = Form("email-domain", "other.example", "evidence", token);
            using var denied = await ordinary.PostAsync(path, form);
            Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
            Assert.Contains("/errors/403", denied.Headers.Location?.OriginalString ?? "");
        }
        foreach (var path in new[] { CreatePath, RevokePath(id) })
        {
            using var form = Form("email-domain", "other.example", "evidence");
            using var missingToken = await admin.PostAsync(path, form);
            Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);
        }
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM email_domain_bans WHERE id = @id", new { id }));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_events WHERE type = 'email_domain.banned'"));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_events WHERE type = 'email_domain.ban_revoked'"));
    }

    [Fact]
    public async Task TypeFiltersAndPaginationKeepTheUnifiedBanIdentities()
    {
        await using var tester = await StartServer();
        using var admin = (await AdminBrowser(tester)).Browser;
        var first = await Ban(admin, "first.example");
        var second = await Ban(admin, "second.example");
        var ip = await Ban(admin, "203.0.113.42", kind: "ip");
        var github = await Ban(admin, "12345", kind: "github");
        var expected = new[] { ("email-domain", first), ("email-domain", second), ("ip", ip), ("github", github) };
        var allPages = new List<(string Kind, long Id)>();
        for (var skip = 0; skip < expected.Length; skip++)
        {
            var rows = RowsOnPage(await admin.GetStringAsync(PagePath + $"?Count=1&Skip={skip}"));
            allPages.Add(Assert.Single(rows));
        }
        Assert.Equal(expected.OrderBy(row => row.Item1).ThenBy(row => row.Item2),
            allPages.OrderBy(row => row.Kind).ThenBy(row => row.Id));

        var firstDomainPage = await admin.GetStringAsync(PagePath + "?kind=email-domain&Count=1");
        Assert.Equal("email-domain", Assert.Single(RowsOnPage(firstDomainPage)).Kind);
        var links = Regex.Matches(firstDomainPage, "\\bhref=\"([^\"]+)\"")
            .Select(match => WebUtility.HtmlDecode(match.Groups[1].Value)).ToArray();
        Assert.Contains(links, link => link.Contains("kind=email-domain", StringComparison.OrdinalIgnoreCase)
            && link.Contains("Skip=1", StringComparison.OrdinalIgnoreCase));
        var secondDomainPage = await admin.GetStringAsync(PagePath + "?kind=email-domain&Count=1&Skip=1");
        Assert.Equal(new[] { first, second }.Order(),
            RowsOnPage(firstDomainPage).Concat(RowsOnPage(secondDomainPage)).Select(row => row.Id).Order());
        Assert.Equal(new[] { ("ip", ip) }, RowsOnPage(await admin.GetStringAsync(PagePath + "?kind=ip")));
        Assert.Equal(new[] { ("github", github) }, RowsOnPage(await admin.GetStringAsync(PagePath + "?kind=github")));
    }

    private async Task<ServerTester> StartServer()
    {
        var tester = Create(nameof(AdditionalBlockedEmailDomainsTests));
        tester.ReuseDatabase = false;
        tester.ConfigureServices = services =>
        {
            foreach (var service in services.Where(service => service.ServiceType == typeof(IHostedService) &&
                         service.ImplementationType != typeof(DatabaseStartupHostedService)).ToArray())
                services.Remove(service);
        };
        await tester.Start();
        return tester;
    }

    private static async Task<(HttpClient Browser, string UserId)> AdminBrowser(ServerTester tester)
    {
        var email = Email("example.com");
        var id = await tester.CreateFakeUserAsync(email, Password, githubVerified: false);
        using var scope = tester.WebApp.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        Assert.True((await users.AddToRoleAsync((await users.FindByIdAsync(id))!, Roles.ServerAdmin)).Succeeded);
        var browser = HttpTestHelpers.CreateBrowser(tester);
        await HttpTestHelpers.LogIn(browser, email, Password);
        return (browser, id);
    }

    private static async Task<long> Ban(HttpClient admin, string target, string reason = "reviewed disposable service", string kind = "email-domain")
    {
        using var response = await Post(admin, CreatePath, kind, target, reason);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = JObject.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(kind, result.Value<string>("kind"));
        var id = result.Value<long>("id");
        Assert.True(id > 0);
        Assert.StartsWith(PagePath, result.Value<string>("redirectUrl"));
        return id;
    }

    private static async Task Revoke(HttpClient admin, long id, string reason = "reviewed ban")
    {
        using var response = await Post(admin, RevokePath(id), null, null, reason);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith(PagePath, JObject.Parse(await response.Content.ReadAsStringAsync()).Value<string>("redirectUrl"));
    }

    private static async Task<HttpResponseMessage> Post(HttpClient admin, string path, string? kind, string? target, string reason)
    {
        var token = HttpTestHelpers.AntiforgeryToken(await admin.GetStringAsync(PagePath));
        using var form = Form(kind, target, reason, token);
        return await admin.PostAsync(path, form);
    }

    private static FormUrlEncodedContent Form(string? kind, string? target, string reason, string? token = null)
    {
        var fields = new Dictionary<string, string> { ["Reason"] = reason };
        if (kind is not null)
            fields["Kind"] = kind;
        if (target is not null)
            fields["Target"] = target;
        if (token is not null)
            fields["__RequestVerificationToken"] = token;
        return new FormUrlEncodedContent(fields);
    }

    private static async Task AssertValidationProblem(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEmpty(JObject.Parse(await response.Content.ReadAsStringAsync())["errors"]!);
    }

    private static async Task<(HttpStatusCode Status, string Body)> Register(ServerTester tester, string email)
    {
        using var browser = HttpTestHelpers.CreateBrowser(tester);
        var page = await browser.GetStringAsync("/register");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = Password,
            ["ConfirmPassword"] = Password,
            ["__RequestVerificationToken"] = HttpTestHelpers.AntiforgeryToken(page)
        });
        using var response = await browser.PostAsync("/register", form);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static (string Kind, long Id)[] RowsOnPage(string html) =>
        Regex.Matches(html, "<tr\\b[^>]*\\bdata-ban-kind=\"[^\"]+\"[^>]*>")
            .Select(match => (
                Kind: WebUtility.HtmlDecode(Regex.Match(match.Value, "\\bdata-ban-kind=\"([^\"]+)\"").Groups[1].Value),
                Id: long.Parse(Regex.Match(match.Value, "\\bdata-ban-id=\"([0-9]+)\"").Groups[1].Value)))
            .ToArray();

    private static Task<string> Snapshot(NpgsqlConnection conn, long id) =>
        conn.QuerySingleAsync<string>("SELECT row_to_json(b)::text FROM email_domain_bans b WHERE id = @id", new { id });

    private static string RevokePath(long id) => CreatePath + $"/email-domain/{id}/revoke";
    private static string Email(string domain) => $"user-{Guid.NewGuid():N}@{domain}";
    private static Task<bool> UserExists(NpgsqlConnection conn, string email) =>
        conn.ExecuteScalarAsync<bool>("""SELECT EXISTS(SELECT 1 FROM "AspNetUsers" WHERE "Email" = @email)""", new { email });
}
