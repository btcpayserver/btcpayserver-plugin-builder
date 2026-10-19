using System.Net;
using System.Net.Http.Headers;
using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;
using PluginBuilder.Authentication;
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
        await Expect(page.GetByRole(AriaRole.Link, new() { Name = "Admin tokens", Exact = true })).ToHaveCountAsync(0);
        await page.Locator("#TokenStatusFilter").ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Inactive", Exact = true }).ClickAsync();
        await Expect(page.Locator("#TokenStatusFilter")).ToHaveTextAsync("Status: Inactive");
        await Expect(page.Locator("#InactiveTokens")).ToContainTextAsync("Expired, revoked and invalidated tokens will appear here.");
        await page.Locator("#CreateToken").ClickAsync();
        var modal = page.Locator("#CreateTokenModal");
        await Expect(modal).ToBeVisibleAsync();
        await modal.Locator("#Creation_Name").FillAsync("Release <agent>");
        await modal.Locator("#Creation_PluginSlug").SelectOptionAsync("ui-mine");
        await modal.Locator("#TokenLifetime").SelectOptionAsync("0");
        await Expect(modal.Locator("#TokenExpiryPreview")).ToHaveTextAsync("Never expires.");
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
        await Expect(page.Locator("#TokenStatusFilter")).ToHaveTextAsync("Status: Active");
        var row = page.Locator($"tr[data-token-id='{id}']");
        await Expect(row).ToBeVisibleAsync();
        await page.Locator("#TokenStatusFilter").ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Inactive", Exact = true }).ClickAsync();
        await Expect(row).ToBeHiddenAsync();
        await page.Locator("#TokenStatusFilter").ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Active", Exact = true }).ClickAsync();
        await Expect(row).ToBeVisibleAsync();
        var reload = await page.ReloadAsync();
        Assert.Equal("GET", reload!.Request.Method);
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM publisher_access_tokens"));
        Assert.DoesNotContain(token, await page.ContentAsync());
        await Expect(page.Locator("#TokenStatusFilter")).ToHaveTextAsync("Status: Active");
        await Expect(row.Locator("[data-token-name]")).ToHaveTextAsync("Release <agent>");
        await row.Locator("[data-copy-id]").ClickAsync();
        Assert.Equal(id.ToString(), await page.EvaluateAsync<string>("() => navigator.clipboard.readText()"));

        using var agent = tester.Server.CreateHttpClient();
        agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync("/api/v1/plugins/ui-mine/builds")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync("/api/v1/plugins/ui-other/builds")).StatusCode);
        await row.GetByRole(AriaRole.Button, new() { Name = "Revoke", Exact = true }).ClickAsync();
        await page.Locator("#ConfirmContinue").ClickAsync();
        await page.Locator("#TokenStatusFilter").ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Inactive", Exact = true }).ClickAsync();
        await Expect(page.Locator("#TokenStatusFilter")).ToHaveTextAsync("Status: Inactive");
        await Expect(row).ToBeVisibleAsync();
        await Expect(row.Locator("[data-token-status]")).ToHaveTextAsync("Revoked");
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.GetAsync("/api/v1/plugins/ui-mine/builds")).StatusCode);
        await page.GetByRole(AriaRole.Link, new() { Name = "2FA", Exact = true }).ClickAsync();
        await Expect(page.Locator("#SectionNav [aria-current='page']")).ToHaveTextAsync("2FA");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllPluginsIsAnExplicitChoiceThatAlsoCoversFuturePlugins(bool alreadyOwnsPlugin)
    {
        await using var tester = CreateTester();
        await tester.StartAsync();
        const string email = "publisher@example.invalid";
        var userId = await tester.CreateConfirmedUser(email);
        var otherId = await tester.CreateConfirmedUser("other@example.invalid");
        await using var conn = await tester.Server.GetService<DBConnectionFactory>().Open();
        await conn.NewPlugin(new PluginSlug("ui-foreign"), otherId);
        if (alreadyOwnsPlugin)
            await conn.NewPlugin(new PluginSlug("ui-existing"), userId);
        await tester.LogIn(email);
        await tester.GoToUrl("/account/api-tokens");
        var page = tester.Page!;
        await page.Locator("#CreateToken").ClickAsync();
        var modal = page.Locator("#CreateTokenModal");
        await Expect(modal.Locator("#Creation_PluginSlug")).ToHaveValueAsync(alreadyOwnsPlugin ? "ui-existing" : "");
        await Expect(modal.Locator("#TokenScopeHelp")).ToBeHiddenAsync();
        await modal.Locator("#Creation_PluginSlug").SelectOptionAsync(PluginBuilderAuthenticationSchemes.AllPublisherPlugins);
        await Expect(modal.Locator("#TokenScopeHelp")).ToBeVisibleAsync();
        await Expect(modal.Locator("#TokenScopeHelp")).ToContainTextAsync("now and in the future");
        await modal.Locator("#Creation_Name").FillAsync("All plugins CI");
        await modal.Locator("button[type='submit']").ClickAsync();
        await Expect(modal.Locator("#TokenSuccess")).ToBeVisibleAsync();
        await Expect(modal.Locator("#TokenSuccess")).ToContainTextAsync("All my plugins (current and future)");
        var token = await modal.Locator("#IssuedToken").InputValueAsync();
        await modal.GetByRole(AriaRole.Button, new() { Name = "Done", Exact = true }).ClickAsync();
        await page.ReloadAsync();
        await Expect(page.Locator("#ActiveTokens tbody tr")).ToContainTextAsync("All my plugins");
        Assert.DoesNotContain(token, await page.ContentAsync());
        var storedScope = await conn.QuerySingleAsync<string?>("SELECT plugin_slug FROM publisher_access_tokens WHERE name = 'All plugins CI'");
        Assert.Null(storedScope);
        using var api = tester.Server.CreateHttpClient();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (alreadyOwnsPlugin)
            Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/v1/plugins/ui-existing/builds")).StatusCode);
        await conn.NewPlugin(new PluginSlug("ui-future"), userId);
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/v1/plugins/ui-future/builds")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.GetAsync("/api/v1/plugins/ui-foreign/builds")).StatusCode);
        await conn.RemovePluginOwner(new PluginSlug("ui-future"), userId);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.GetAsync("/api/v1/plugins/ui-future/builds")).StatusCode);
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
        await modal.Locator("#TokenLifetime").SelectOptionAsync("custom");
        await Expect(modal.Locator("#Creation_ExpiresInDays")).ToBeVisibleAsync();
        await modal.Locator("#Creation_ExpiresInDays").FillAsync("0");
        Assert.False(await modal.Locator("#Creation_ExpiresInDays").EvaluateAsync<bool>("element => element.checkValidity()"));
        await modal.Locator("#Creation_ExpiresInDays").FillAsync("12");
        await Expect(modal.Locator("#TokenExpiryPreview")).ToContainTextAsync("Expires");
        await modal.Locator("button[type='submit']").ClickAsync();
        await Expect(modal.Locator("#TokenSuccess")).ToBeVisibleAsync();
        var expiry = await conn.ExecuteScalarAsync<DateTime>("SELECT expires_at FROM publisher_access_tokens WHERE name = 'Copy fallback'");
        Assert.InRange((expiry - DateTime.UtcNow).TotalDays, 11.9, 12.1);
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

    [Fact]
    public async Task LostCreationResponseStaysBlockedWhenTheModalIsReopened()
    {
        await using var tester = CreateTester();
        await tester.StartAsync();
        const string email = "publisher@example.invalid";
        var userId = await tester.CreateConfirmedUser(email);
        await using var conn = await tester.Server.GetService<DBConnectionFactory>().Open();
        await conn.NewPlugin(new PluginSlug("ui-lost-response"), userId);
        await tester.LogIn(email);
        const string tokenPath = "/account/api-tokens";
        await tester.GoToUrl(tokenPath);
        var page = tester.Page!;
        var creationRequests = 0;
        await page.RouteAsync("**" + tokenPath, async route =>
        {
            if (route.Request.Method != "POST")
            {
                await route.ContinueAsync();
                return;
            }

            Interlocked.Increment(ref creationRequests);
            var response = await route.FetchAsync();
            Assert.Equal(200, response.Status);
            await route.FulfillAsync(new()
            {
                Status = 503,
                ContentType = "text/plain",
                Body = "Gateway failure after token creation."
            });
        });

        await page.Locator("#CreateToken").ClickAsync();
        var modal = page.Locator("#CreateTokenModal");
        var form = modal.Locator("#CreateTokenForm");
        var create = form.Locator("button[type='submit']");
        var warning = modal.Locator("#TokenCreationError");
        await modal.Locator("#Creation_Name").FillAsync("Lost response");
        await create.ClickAsync();
        await Expect(warning).ToContainTextAsync("Reload this page and check the token list.");
        await Expect(warning).ToBeVisibleAsync();
        await Expect(create).ToBeDisabledAsync();
        var id = await conn.QuerySingleAsync<Guid>("SELECT id FROM publisher_access_tokens WHERE name = 'Lost response'");
        await Expect(modal.Locator("#TokenSuccess")).ToBeHiddenAsync();
        await form.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        await Expect(modal).ToBeHiddenAsync();

        await page.Locator("#CreateToken").ClickAsync();
        await Expect(warning).ToBeVisibleAsync();
        await Expect(create).ToBeDisabledAsync();
        await modal.Locator("#Creation_Name").FillAsync("Retry without checking");
        await form.EvaluateAsync("element => element.requestSubmit()");
        Assert.Equal(1, creationRequests);
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM publisher_access_tokens"));

        var reload = await page.ReloadAsync();
        Assert.Equal("GET", reload!.Request.Method);
        await Expect(page.Locator($"#ActiveTokens tr[data-token-id='{id}']")).ToContainTextAsync("Lost response");
        await page.Locator("#CreateToken").ClickAsync();
        await Expect(warning).ToBeHiddenAsync();
        await Expect(create).ToBeEnabledAsync();
        Assert.Equal(1, creationRequests);
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
