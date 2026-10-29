using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;
using Newtonsoft.Json.Linq;
using PluginBuilder.DataModels;
using PluginBuilder.HostedServices;
using PluginBuilder.Services;
using PluginBuilder.Util;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace PluginBuilder.Tests.AdminTests;

[Collection("Playwright Tests")]
public class AdminUsersUITests(ITestOutputHelper output) : UnitTestBase(output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LockAndUnlockRequireAReasonAndKeepBansSeparate(bool signInLockedOut)
    {
        await using var tester = CreateTester("UsersLockUi");
        await tester.StartAsync();
        var adminEmail = await tester.CreateServerAdminAsync();
        var owner = await tester.CreateConfirmedUser("owner@example.test");
        await using var conn = await tester.Server.GetService<DBConnectionFactory>().Open();
        if (signInLockedOut)
            await conn.ExecuteAsync("""UPDATE "AspNetUsers" SET "LockoutEnd" = clock_timestamp() + INTERVAL '1 hour' WHERE "Id" = @owner""",
                new { owner });
        var slug = new PluginSlug("ui-lock");
        await conn.NewPlugin(slug, owner);
        await conn.NewBuild(slug, new PluginBuildParameters(ServerTester.RepoUrl), owner);
        await tester.LogIn(adminEmail);
        await tester.GoToUrl("/admin/users");
        var page = tester.Page!;
        var row = page.Locator($"tr[data-user-id='{owner}']");
        var modal = page.Locator("#UserModerationModal");
        await Expect(page.Locator("#AdminUsers #SectionNav, #AdminUsers #Bans")).ToHaveCountAsync(0);
        await Expect(row.GetByRole(AriaRole.Cell, new() { Name = signInLockedOut ? "Sign-in locked out" : "Active", Exact = true })).ToBeVisibleAsync();

        await page.Locator("#SearchText").FillAsync("owner@example.test");
        await page.Locator("#SearchText").PressAsync("Enter");
        await row.GetByRole(AriaRole.Button, new() { Name = "Actions for owner@example.test", Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Link, new() { Name = "Change Email", Exact = true })).ToBeInViewportAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Edit Roles", Exact = true }).ClickAsync();
        await Expect(page).ToHaveURLAsync(new Regex($"/admin/editroles/{owner}$"));
        await page.GoBackAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        await row.GetByRole(AriaRole.Button, new() { Name = "Actions for owner@example.test", Exact = true }).ClickAsync();
        await Expect(row.GetByRole(AriaRole.Button, new() { Name = "Unlock", Exact = true })).ToHaveCountAsync(signInLockedOut ? 1 : 0);
        await row.GetByRole(AriaRole.Button, new() { Name = "Lock", Exact = true }).ClickAsync();
        await Expect(modal).ToBeVisibleAsync();
        await Expect(modal.Locator("#ModerationTarget")).ToContainTextAsync("owner@example.test");
        await modal.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        await Expect(modal).ToBeHiddenAsync();
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_account_locks"));

        await row.GetByRole(AriaRole.Button, new() { Name = "Actions for owner@example.test", Exact = true }).ClickAsync();
        await row.GetByRole(AriaRole.Button, new() { Name = "Lock", Exact = true }).ClickAsync();
        await modal.Locator("#ModerationReason").FillAsync("   ");
        await modal.Locator("#ModerationSubmit").ClickAsync();
        await Expect(modal.Locator("#ModerationError")).ToBeVisibleAsync();
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_account_locks"));
        await modal.Locator("#ModerationReason").FillAsync("reviewing build activity");
        await modal.Locator("#ModerationSubmit").ClickAsync();
        await Expect(row.GetByText("Locked", new() { Exact = true })).ToBeVisibleAsync();
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_account_locks"));
        Assert.Equal("failed", await conn.QuerySingleAsync<string>("SELECT state FROM builds"));
        var audit = JObject.Parse(await conn.QuerySingleAsync<string>(
            "SELECT data::text FROM admin_events WHERE type = 'user.locked'"));
        Assert.Equal("reviewing build activity", audit.Value<string>("reason"));
        Assert.Equal(await conn.QuerySingleAsync<string>(
            """SELECT "Id" FROM "AspNetUsers" WHERE "Email" = @adminEmail""", new { adminEmail }), audit.Value<string>("by"));
        Assert.Equal(JTokenType.Null, audit["tokenId"]!.Type);
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT (SELECT count(*) FROM github_bans) + (SELECT count(*) FROM ip_bans)"));

        await row.GetByRole(AriaRole.Button, new() { Name = "Actions for owner@example.test", Exact = true }).ClickAsync();
        await Expect(row.GetByRole(AriaRole.Button, new() { Name = "Lock", Exact = true })).ToHaveCountAsync(0);
        await row.GetByRole(AriaRole.Button, new() { Name = "Unlock", Exact = true }).ClickAsync();
        await modal.Locator("#ModerationReason").FillAsync("review complete");
        await modal.Locator("#ModerationSubmit").ClickAsync();
        await Expect(row.GetByRole(AriaRole.Cell, new() { Name = "Active", Exact = true })).ToBeVisibleAsync();
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_account_locks"));
        Assert.Equal("review complete", await conn.QuerySingleAsync<string>(
            "SELECT data->>'reason' FROM admin_events WHERE type = 'user.unlocked'"));
    }

    [Fact]
    public async Task BurnContainsLinkedAccountsAndRevokingBansDoesNotUnlockThem()
    {
        await using var tester = CreateTester("UsersBurnUi");
        await tester.StartAsync();
        var adminEmail = await tester.CreateServerAdminAsync();
        var owner = await tester.CreateConfirmedUser("probe@example.test");
        var linked = await tester.CreateConfirmedUser("linked@example.test");
        var shared = await tester.CreateConfirmedUser("shared@example.test");
        await using var conn = await tester.Server.GetService<DBConnectionFactory>().Open();
        await conn.ExecuteAsync("""
            UPDATE "AspNetUsers" SET "GithubUserId" = 321, "GithubGistUrl" = 'https://gist.github.com/probe/abcdef123456',
              "AccountDetail" = '{"github":"probe"}'::jsonb WHERE "Id" = ANY(@identities);
            UPDATE "AspNetUsers" SET "SignupIp" = '203.0.113.42'::inet WHERE "Id" = ANY(@sameIp)
            """, new { identities = new[] { owner, linked }, sameIp = new[] { owner, shared } });
        var slug = new PluginSlug("ui-burn");
        await conn.NewPlugin(slug, owner);
        await conn.NewBuild(slug, new PluginBuildParameters(ServerTester.RepoUrl), owner);
        var running = await conn.NewBuild(slug, new PluginBuildParameters(ServerTester.RepoUrl), linked);
        await conn.UpdateBuild(new FullBuildId(slug, running), BuildStates.Running, null);
        var finished = await conn.NewBuild(slug, new PluginBuildParameters(ServerTester.RepoUrl), owner);
        await conn.UpdateBuild(new FullBuildId(slug, finished), BuildStates.Uploaded, null);
        await conn.NewBuild(slug, new PluginBuildParameters(ServerTester.RepoUrl), shared);
        await tester.LogIn(adminEmail);
        await tester.GoToUrl("/admin/users");
        var page = tester.Page!;
        var modal = page.Locator("#UserModerationModal");
        var ownerRow = page.Locator($"tr[data-user-id='{owner}']");
        await Expect(ownerRow.GetByRole(AriaRole.Link, new() { Name = "@probe", Exact = true })).ToBeVisibleAsync();
        await ownerRow.GetByRole(AriaRole.Button, new() { Name = "Actions for probe@example.test", Exact = true }).ClickAsync();
        await ownerRow.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Burn") }).ClickAsync();
        await Expect(modal.Locator("#ModerationSignupIp")).ToHaveTextAsync("203.0.113.42");
        await Expect(modal).ToContainTextAsync("unfinished builds");
        await modal.Locator("#ModerationReason").FillAsync("confirmed network probe");
        await modal.Locator("#ModerationSubmit").ClickAsync();
        await Expect(ownerRow.GetByText("Locked", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(page.Locator($"tr[data-user-id='{linked}']").GetByText("Locked", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(page.Locator($"tr[data-user-id='{shared}']")).ToBeVisibleAsync();
        await Expect(page.Locator($"tr[data-user-id='{shared}']").GetByRole(AriaRole.Cell, new() { Name = "Active", Exact = true })).ToBeVisibleAsync();
        Assert.Equal(new[] { "failed", "failed", "uploaded", "queued" },
            await conn.QueryAsync<string>("SELECT state FROM builds ORDER BY id"));
        var audit = JObject.Parse(await conn.QuerySingleAsync<string>(
            "SELECT data::text FROM admin_events WHERE type = 'user.burned'"));
        Assert.Equal("confirmed network probe", audit.Value<string>("reason"));
        Assert.Equal(new[] { owner, linked }.Order(), audit["lockedUserIds"]!.Values<string>().Order());

        await page.Locator("#AdminNav-Moderation").ClickAsync();
        await Expect(page).ToHaveURLAsync(new Regex("/admin/moderation$"));
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Moderation", Exact = true })).ToBeVisibleAsync();
        await Expect(page.Locator("#AdminNav-Moderation")).ToHaveClassAsync(new Regex("active"));
        var githubId = await conn.QuerySingleAsync<long>("SELECT id FROM github_bans");
        var ipId = await conn.QuerySingleAsync<long>("SELECT id FROM ip_bans WHERE network = '203.0.113.42/32'::cidr");
        Assert.Equal(githubId, ipId); // Separate tables can assign the same ID to different kinds of ban.
        var githubRow = page.Locator($"#Bans tr[data-ban-kind='github'][data-ban-id='{githubId}']");
        var ipRow = page.Locator($"#Bans tr[data-ban-kind='ip'][data-ban-id='{ipId}']");
        await Expect(githubRow.GetByRole(AriaRole.Cell, new() { Name = "GitHub", Exact = true })).ToBeVisibleAsync();
        await Expect(ipRow.GetByRole(AriaRole.Cell, new() { Name = "IP", Exact = true })).ToBeVisibleAsync();
        await Expect(githubRow).ToContainTextAsync("321");
        await Expect(githubRow).ToContainTextAsync("confirmed network probe");
        await Expect(ipRow).ToContainTextAsync("203.0.113.42/32");
        await githubRow.GetByRole(AriaRole.Button, new() { Name = "Revoke", Exact = true }).ClickAsync();
        await modal.Locator("#ModerationReason").FillAsync("identity ban reviewed");
        await modal.Locator("#ModerationSubmit").ClickAsync();
        await Expect(githubRow).ToHaveCountAsync(0);
        await Expect(ipRow).ToBeVisibleAsync();
        Assert.Equal(2, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_account_locks"));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM github_bans"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM ip_bans"));

        await conn.ExecuteAsync("INSERT INTO ip_bans (network, reason) VALUES ('198.51.100.80/32'::cidr, 'unrelated network')");
        await tester.GoToUrl("/admin/moderation?Count=1&Skip=1");
        await Expect(ipRow).ToContainTextAsync("203.0.113.42/32");
        await ipRow.GetByRole(AriaRole.Button, new() { Name = "Revoke", Exact = true }).ClickAsync();
        await Expect(modal).ToContainTextAsync("unlock");
        await modal.Locator("#ModerationReason").FillAsync("network ban reviewed");
        await modal.Locator("#ModerationSubmit").ClickAsync();
        await Expect(ipRow).ToHaveCountAsync(0);
        await Expect(page.Locator("#Bans")).ToContainTextAsync("No bans on this page.");
        Assert.Equal("/admin/moderation?Count=1&Skip=1", new Uri(page.Url).PathAndQuery);
        var previous = page.GetByRole(AriaRole.Link, new() { Name = "Prev", Exact = true });
        await Expect(previous).ToBeVisibleAsync();
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM ip_bans"));
        Assert.Equal(2, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_account_locks"));
        Assert.Equal(2, await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM admin_events WHERE type IN ('github.ban_revoked', 'ip.ban_revoked') AND data->>'by' IS NOT NULL"));
        await previous.ClickAsync();
        await Expect(page.Locator("#Bans")).ToContainTextAsync("198.51.100.80/32");
    }

    [Fact]
    public async Task ManualIpBanValidatesTheNetworkAndBurnShowsIneligibleEvidenceWarning()
    {
        await using var tester = CreateTester("UsersIpUi");
        await tester.StartAsync();
        await tester.LogIn(await tester.CreateServerAdminAsync());
        var owner = await tester.CreateConfirmedUser("internal@example.test");
        await using var conn = await tester.Server.GetService<DBConnectionFactory>().Open();
        await conn.ExecuteAsync("""UPDATE "AspNetUsers" SET "SignupIp" = '10.0.0.5'::inet WHERE "Id" = @owner""", new { owner });
        await tester.GoToUrl("/admin/moderation");
        var page = tester.Page!;
        var modal = page.Locator("#UserModerationModal");
        await page.Locator("#BanIp").ClickAsync();
        await modal.Locator("#ModerationNetwork").FillAsync("198.51.100.0/24");
        await modal.Locator("#ModerationReason").FillAsync("confirmed abuse");
        await modal.Locator("#ModerationSubmit").ClickAsync();
        await Expect(modal.Locator("#ModerationError")).ToBeVisibleAsync();
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM ip_bans"));
        await modal.Locator("#ModerationNetwork").FillAsync("2001:db8:42::123/64");
        await modal.Locator("#ModerationSubmit").ClickAsync();
        await Expect(page.Locator("#Bans")).ToContainTextAsync("2001:db8:42::/64");
        await Expect(page).ToHaveURLAsync(new Regex("/admin/moderation$"));
        Assert.Equal("2001:db8:42::/64", await conn.QuerySingleAsync<string>("SELECT network::text FROM ip_bans"));

        await page.Locator("#AdminNav-Users").ClickAsync();
        await page.Locator($"tr[data-user-id='{owner}']").GetByRole(AriaRole.Button,
            new() { Name = "Actions for internal@example.test", Exact = true }).ClickAsync();
        await page.Locator($"tr[data-user-id='{owner}']").GetByRole(AriaRole.Button,
            new() { NameRegex = new Regex("^Burn") }).ClickAsync();
        await modal.Locator("#ModerationReason").FillAsync("confirmed abuse");
        await modal.Locator("#ModerationSubmit").ClickAsync();
        await Expect(page.Locator("#mainContent")).ToContainTextAsync("No eligible signup IP from the last seven days");
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_account_locks WHERE user_id = @owner", new { owner }));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM ip_bans"));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM github_bans"));
    }

    [Fact]
    public async Task ModerationRequiresAdminRoleAntiforgeryAndAnUnprotectedTarget()
    {
        await using var tester = CreateTester("UsersGuardUi");
        await tester.StartAsync();
        var adminEmail = await tester.CreateServerAdminAsync();
        var owner = await tester.CreateConfirmedUser("ordinary@example.test");
        var pending = await tester.Server.CreateFakeUserAsync("pending@example.test", confirmEmail: false, githubVerified: false);
        await using var conn = await tester.Server.GetService<DBConnectionFactory>().Open();
        var admin = await conn.QuerySingleAsync<string>(
            """SELECT "Id" FROM "AspNetUsers" WHERE "Email" = @adminEmail""", new { adminEmail });
        await tester.LogIn(adminEmail);
        await tester.GoToUrl("/admin/users");
        var page = tester.Page!;
        var protectedRow = page.Locator($"tr[data-user-id='{admin}']");
        await Expect(page.Locator("table thead th")).ToHaveTextAsync(new[] { "Email", "GitHub", "Status", "Actions" });
        await Expect(protectedRow.GetByText("ServerAdmin", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(page.Locator($"tr[data-user-id='{pending}']").GetByText("Unconfirmed", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(page.Locator($"tr[data-user-id='{owner}'] td:first-child .badge")).ToHaveCountAsync(0);
        await protectedRow.GetByRole(AriaRole.Button, new() { Name = $"Actions for {adminEmail}", Exact = true }).ClickAsync();
        await Expect(protectedRow.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^(Lock|Burn)") })).ToHaveCountAsync(0);
        var csrf = HttpTestHelpers.AntiforgeryToken(await page.ContentAsync());
        foreach (var operation in new[] { "lock", "burn" })
        {
            var form = page.Context.APIRequest.CreateFormData();
            form.Set("Reason", "direct request");
            var url = new Uri(tester.ServerUri!, $"/admin/users/{owner}/{operation}").ToString();
            Assert.Equal(400, (await page.Context.APIRequest.PostAsync(url, new() { Form = form, MaxRedirects = 0 })).Status);
            form.Set("__RequestVerificationToken", csrf);
            url = new Uri(tester.ServerUri!, $"/admin/users/{admin}/{operation}").ToString();
            Assert.Equal(409, (await page.Context.APIRequest.PostAsync(url, new() { Form = form, MaxRedirects = 0 })).Status);
        }
        await tester.Logout();
        await tester.LogIn("ordinary@example.test");
        await tester.GoToUrl("/dashboard");
        csrf = HttpTestHelpers.AntiforgeryToken(await page.ContentAsync());
        var ordinary = page.Context.APIRequest.CreateFormData();
        ordinary.Set("Reason", "direct request");
        ordinary.Set("__RequestVerificationToken", csrf);
        var denied = await page.Context.APIRequest.PostAsync(new Uri(tester.ServerUri!, $"/admin/users/{owner}/burn").ToString(),
            new() { Form = ordinary, MaxRedirects = 0 });
        Assert.Equal(302, denied.Status);
        Assert.Contains("/errors/403", denied.Headers["location"], StringComparison.Ordinal);
        denied = await page.Context.APIRequest.GetAsync(new Uri(tester.ServerUri!, "/admin/moderation").ToString(),
            new() { MaxRedirects = 0 });
        Assert.Equal(302, denied.Status);
        Assert.Contains("/errors/403", denied.Headers["location"], StringComparison.Ordinal);
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>(
            "SELECT (SELECT count(*) FROM admin_account_locks) + (SELECT count(*) FROM github_bans) + (SELECT count(*) FROM ip_bans)"));
    }

    private PlaywrightTester CreateTester(string name)
    {
        var tester = new PlaywrightTester(Log, Create(name));
        tester.Server.ReuseDatabase = false;
        tester.Server.ConfigureServices = services =>
        {
            foreach (var descriptor in services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                         && descriptor.ImplementationType != typeof(DatabaseStartupHostedService)).ToArray())
                services.Remove(descriptor);
        };
        return tester;
    }
}
