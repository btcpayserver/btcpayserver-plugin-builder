using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;
using PluginBuilder.HostedServices;
using Xunit;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace PluginBuilder.Tests.AdminTests;

[Collection("Playwright Tests")]
public class AdminBlockedEmailDomainsUITests(ITestOutputHelper output) : UnitTestBase(output)
{
    [Fact]
    public async Task BansCanBeCreatedFilteredAndRevokedByType()
    {
        await using var tester = new PlaywrightTester(Log, Create("UnifiedBansUi"));
        tester.Server.ReuseDatabase = false;
        tester.Server.ConfigureServices = services =>
        {
            foreach (var descriptor in services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                         && descriptor.ImplementationType != typeof(DatabaseStartupHostedService)).ToArray())
                services.Remove(descriptor);
        };
        await tester.StartAsync();
        await tester.LogIn(await tester.CreateServerAdminAsync());
        await tester.GoToUrl("/admin/moderation");
        var page = tester.Page!;
        var modal = page.Locator("#UserModerationModal");
        var filter = page.Locator("#TypeOptionsToggle");
        var domainRow = page.Locator("#Bans tr[data-ban-kind='email-domain']").Filter(new() { HasText = "temp.example" });

        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Moderation", Exact = true })).ToBeVisibleAsync();
        await Expect(page.Locator("#BlockedEmailDomains")).ToHaveCountAsync(0);
        await Expect(filter).ToHaveTextAsync("All types");
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Filter", Exact = true })).ToHaveCountAsync(0);
        await CreateBan("email-domain", "TEMP.Example.", "disposable email service");
        await Expect(domainRow).ToBeVisibleAsync();
        await Expect(domainRow).ToContainTextAsync("disposable email service");
        await Expect(filter).ToHaveTextAsync("Email domain");
        await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(domainRow).ToBeVisibleAsync();
        await Expect(filter).ToHaveTextAsync("Email domain");

        await page.Locator("#Ban").ClickAsync();
        await modal.Locator("#ModerationBanType").SelectOptionAsync("email-domain");
        await modal.Locator("#ModerationBanTarget").FillAsync("https://invalid.example");
        await modal.Locator("#ModerationReason").FillAsync("invalid target must not be saved");
        await modal.Locator("#ModerationSubmit").ClickAsync();
        await Expect(modal.Locator("#ModerationError")).ToBeVisibleAsync();
        await Expect(modal).ToBeVisibleAsync();
        await Expect(modal.Locator("#ModerationBanTarget")).ToHaveValueAsync("https://invalid.example");
        await Expect(page.Locator("#Bans tr[data-ban-kind='email-domain']")).ToHaveCountAsync(1);
        await modal.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        await Expect(modal).ToBeHiddenAsync();

        await CreateBan("ip", "203.0.113.42", "abusive registrations");
        await Expect(page.Locator("#Bans tr[data-ban-kind='ip']")).ToContainTextAsync("203.0.113.42/32");
        await Expect(filter).ToHaveTextAsync("IP");
        await CreateBan("github", "ui-probe", "banned identity");
        await Expect(page.Locator("#Bans tr[data-ban-kind='github']")).ToContainTextAsync("@ui-probe");
        await Expect(filter).ToHaveTextAsync("GitHub");

        await FilterByType("All types");
        await Expect(page.Locator("#Bans tr[data-ban-kind]")).ToHaveCountAsync(3);
        await Expect(page.Locator("#Bans table")).ToHaveCountAsync(1);
        await FilterByType("Email domain");
        await Expect(domainRow).ToBeVisibleAsync();
        await Expect(page.Locator("#Bans tr[data-ban-kind]")).ToHaveCountAsync(1);
        await Expect(page.Locator("#Bans tr[data-ban-kind='github'], #Bans tr[data-ban-kind='ip']")).ToHaveCountAsync(0);

        await domainRow.GetByRole(AriaRole.Button,
            new() { Name = "Revoke Email domain ban for temp.example", Exact = true }).ClickAsync();
        await modal.Locator("#ModerationReason").FillAsync("   ");
        await modal.Locator("#ModerationSubmit").ClickAsync();
        await Expect(modal.Locator("#ModerationError")).ToBeVisibleAsync();
        await Expect(domainRow).ToBeVisibleAsync();
        await modal.Locator("#ModerationReason").FillAsync("domain reviewed");
        await modal.Locator("#ModerationSubmit").ClickAsync();
        await Expect(domainRow).ToHaveCountAsync(0);
        await Expect(page.Locator("#Bans")).ToContainTextAsync("No active bans.");
        await Expect(filter).ToHaveTextAsync("Email domain");
        await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(filter).ToHaveTextAsync("Email domain");
        await Expect(domainRow).ToHaveCountAsync(0);

        async Task FilterByType(string label)
        {
            await filter.ClickAsync();
            await page.Locator(".dropdown-menu").GetByRole(AriaRole.Link, new() { Name = label, Exact = true }).ClickAsync();
        }

        async Task CreateBan(string kind, string target, string reason)
        {
            await page.Locator("#Ban").ClickAsync();
            await modal.Locator("#ModerationBanType").SelectOptionAsync(kind);
            await modal.Locator(kind == "ip" ? "#ModerationNetwork" : "#ModerationBanTarget").FillAsync(target);
            await modal.Locator("#ModerationReason").FillAsync(reason);
            await modal.Locator("#ModerationSubmit").ClickAsync();
            await Expect(modal).ToBeHiddenAsync();
        }
    }
}
