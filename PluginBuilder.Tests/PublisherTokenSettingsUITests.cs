using System.Net;
using System.Net.Http.Headers;
using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;
using PluginBuilder.HostedServices;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace PluginBuilder.Tests;

[Collection("Playwright Tests")]
public class PublisherTokenSettingsUITests(ITestOutputHelper output) : UnitTestBase(output)
{
    [Fact]
    public async Task AccountNavigationLeadsToScopedTokenCreationCopyAndRevocation()
    {
        await using var tester = CreateTester();
        await tester.StartAsync();
        const string email = "publisher@example.invalid";
        var userId = await tester.CreateConfirmedUser(email);
        await using var conn = await tester.Server.GetService<DBConnectionFactory>().Open();
        await conn.NewPlugin(new PluginSlug("ui-mine"), userId);
        await conn.NewPlugin(new PluginSlug("ui-other"), userId);
        await tester.LogIn(email);
        var page = tester.Page!;
        await page.Context.GrantPermissionsAsync(["clipboard-read", "clipboard-write"], new()
        {
            Origin = tester.ServerUri!.GetLeftPart(UriPartial.Authority)
        });
        await page.Locator("#Nav-Account").ClickAsync();
        await page.Locator("#Nav-ManageAccount").ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "API tokens", Exact = true }).ClickAsync();
        await Expect(page.Locator("#SectionNav [aria-current='page']")).ToHaveTextAsync("API tokens");
        await page.Locator("#CreateToken").ClickAsync();
        var modal = page.Locator("#CreateTokenModal");
        await Expect(modal).ToBeVisibleAsync();
        await modal.Locator("#Creation_Name").FillAsync("Release <agent>");
        await modal.Locator("#Creation_PluginSlug").SelectOptionAsync("ui-mine");
        await modal.Locator("#Creation_ExpiresInDays").SelectOptionAsync("0");
        var response = await page.RunAndWaitForResponseAsync(
            () => modal.Locator("button[type='submit']").ClickAsync(),
            result => result.Request.Method == "POST" && new Uri(result.Url).AbsolutePath == "/account/api-tokens");
        Assert.Equal(200, response.Status);
        Assert.False(response.Request.IsNavigationRequest);
        await Expect(modal.Locator("#TokenSuccess")).ToBeVisibleAsync();
        var token = await modal.Locator("#IssuedToken").InputValueAsync();
        var id = await conn.ExecuteScalarAsync<Guid>("SELECT id FROM publisher_access_tokens WHERE name = 'Release <agent>'");
        Assert.Null(await conn.ExecuteScalarAsync<DateTime?>("SELECT expires_at FROM publisher_access_tokens WHERE id = @id", new { id }));
        await modal.GetByRole(AriaRole.Button, new() { Name = "Copy token", Exact = true }).ClickAsync();
        Assert.Equal(token, await page.EvaluateAsync<string>("() => navigator.clipboard.readText()"));
        await modal.GetByRole(AriaRole.Button, new() { Name = "Done", Exact = true }).ClickAsync();
        await Expect(modal).ToBeHiddenAsync();
        await Expect(page.Locator("#IssuedToken, #SecretCopyText")).ToHaveCountAsync(0);
        var reload = await page.ReloadAsync();
        Assert.Equal("GET", reload!.Request.Method);
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM publisher_access_tokens"));
        Assert.DoesNotContain(token, await page.ContentAsync());
        await Expect(page.Locator("#ActiveTokensTab")).ToHaveTextAsync("Active (1)");
        var row = page.Locator($"tr[data-token-id='{id}']");
        await Expect(row.Locator("[data-token-name]")).ToHaveTextAsync("Release <agent>");

        using var agent = tester.Server.CreateHttpClient();
        agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync("/api/v1/plugins/ui-mine/builds")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync("/api/v1/plugins/ui-other/builds")).StatusCode);
        await row.GetByRole(AriaRole.Button, new() { Name = "Revoke", Exact = true }).ClickAsync();
        await page.Locator("#ConfirmContinue").ClickAsync();
        await page.Locator("#InactiveTokensTab").ClickAsync();
        await Expect(row.Locator("[data-token-status]")).ToHaveTextAsync("Revoked");
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.GetAsync("/api/v1/plugins/ui-mine/builds")).StatusCode);
        await page.GetByRole(AriaRole.Link, new() { Name = "2FA", Exact = true }).ClickAsync();
        await Expect(page.Locator("#SectionNav [aria-current='page']")).ToHaveTextAsync("2FA");
    }

    [Fact]
    public async Task ModalRetainsServerValidationAndClearsTheSecretWhenClosed()
    {
        await using var tester = CreateTester();
        await tester.StartAsync();
        const string email = "publisher@example.invalid";
        var userId = await tester.CreateConfirmedUser(email);
        await using var conn = await tester.Server.GetService<DBConnectionFactory>().Open();
        await conn.NewPlugin(new PluginSlug("ui-validation"), userId);
        await tester.LogIn(email);
        await tester.GoToUrl("/account/api-tokens");
        var page = tester.Page!;
        await page.Locator("#CreateToken").ClickAsync();
        var modal = page.Locator("#CreateTokenModal");
        await modal.Locator("#Creation_Name").FillAsync("   ");
        await modal.Locator("button[type='submit']").ClickAsync();
        await Expect(modal.Locator("#TokenValidation li")).ToHaveCountAsync(1);
        await Expect(modal.Locator("#Creation_Name")).ToHaveValueAsync("   ");
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM publisher_access_tokens"));
        await modal.Locator("#Creation_Name").FillAsync("Copy fallback");
        await modal.Locator("button[type='submit']").ClickAsync();
        await Expect(modal.Locator("#TokenSuccess")).ToBeVisibleAsync();
        var token = await modal.Locator("#IssuedToken").InputValueAsync();
        await page.EvaluateAsync("""
            () => Object.defineProperty(navigator, 'clipboard', {
                configurable: true,
                value: { writeText: () => Promise.reject(new DOMException('Clipboard denied', 'NotAllowedError')) }
            })
            """);
        await modal.GetByRole(AriaRole.Button, new() { Name = "Copy token", Exact = true }).ClickAsync();
        await Expect(modal.Locator("#SecretCopyFallback")).ToBeVisibleAsync();
        Assert.Equal(token, await modal.Locator("#SecretCopyText").InputValueAsync());
        await modal.GetByRole(AriaRole.Button, new() { Name = "Done", Exact = true }).ClickAsync();
        await Expect(modal).ToBeHiddenAsync();
        Assert.DoesNotContain(token, await page.ContentAsync());
        await page.Locator("#CreateToken").ClickAsync();
        await Expect(modal.Locator("#Creation_Name")).ToHaveValueAsync("");
        await Expect(modal.Locator("#TokenSuccess")).ToBeHiddenAsync();
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM publisher_access_tokens"));
    }

    private PlaywrightTester CreateTester()
    {
        var tester = new PlaywrightTester(Log, Create());
        tester.Server.ReuseDatabase = false;
        tester.Server.ConfigureServices = services =>
        {
            foreach (var descriptor in services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService) &&
                         descriptor.ImplementationType != typeof(DatabaseStartupHostedService)).ToArray())
                services.Remove(descriptor);
        };
        return tester;
    }
}
