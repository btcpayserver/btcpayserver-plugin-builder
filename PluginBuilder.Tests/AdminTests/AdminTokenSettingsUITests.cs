using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;
using PluginBuilder.HostedServices;
using PluginBuilder.Services;
using Xunit;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace PluginBuilder.Tests.AdminTests;

[Collection("Playwright Tests")]
public class AdminTokenSettingsUITests(ITestOutputHelper output) : UnitTestBase(output)
{
    [Fact]
    public async Task OwnerCanCreateCopyAndRevokeTokenWithoutReloadingTheModalOrResubmitting()
    {
        await using var tester = CreateTester("AdminTokenUi");
        await tester.StartAsync();
        var email = await tester.CreateServerAdminAsync();
        await tester.LogIn(email);
        var page = tester.Page!;
        await page.Context.GrantPermissionsAsync(["clipboard-read", "clipboard-write"], new()
        {
            Origin = tester.ServerUri!.GetLeftPart(UriPartial.Authority)
        });
        await using var conn = await tester.Server.GetService<DBConnectionFactory>().Open();
        const string tokenPath = "/account/admin-access-tokens";
        const string name = "UI agent <review>";

        await tester.GoToUrl(tokenPath);
        await page.Locator("#CreateToken").ClickAsync();
        var modal = page.Locator("#CreateTokenModal");
        await Expect(modal).ToBeVisibleAsync();
        await modal.EvaluateAsync("""
            element => {
                window.originalTokenModal = element;
                window.tokenModalTransitions = 0;
                for (const event of ['show.bs.modal', 'hide.bs.modal'])
                    element.addEventListener(event, () => window.tokenModalTransitions++);
            }
            """);
        await modal.Locator("#Creation_Name").FillAsync(name);
        await modal.Locator("#TokenLifetime").SelectOptionAsync("7");
        var creation = await page.RunAndWaitForResponseAsync(
            () => modal.Locator("#CreateTokenForm").GetByRole(AriaRole.Button, new() { Name = "Create token", Exact = true }).ClickAsync(),
            response => response.Request.Method == "POST" && new Uri(response.Url).AbsolutePath == tokenPath);
        Assert.False(creation.Request.IsNavigationRequest, "Creation should keep the current page and modal open.");
        Assert.Equal(200, creation.Status);
        await Expect(modal.Locator("#TokenSuccess")).ToBeVisibleAsync();
        Assert.True(await modal.EvaluateAsync<bool>("element => element === window.originalTokenModal && window.tokenModalTransitions === 0"),
            "The success step must reuse the open modal without hiding or reopening it.");
        var token = await modal.Locator("#IssuedToken").InputValueAsync();
        Assert.True(token.StartsWith("pb_admin_", StringComparison.Ordinal) && token.Length == 73,
            "Creation should display an admin token with the expected format.");
        var id = await conn.ExecuteScalarAsync<Guid>("SELECT id FROM admin_access_tokens WHERE name = @name", new { name });
        var expires = await conn.ExecuteScalarAsync<DateTime>("SELECT expires_at FROM admin_access_tokens WHERE id = @id", new { id });
        Assert.InRange((expires - DateTime.UtcNow).TotalDays, 6.9, 7.1);
        var row = page.Locator($"tr[data-token-id='{id}']");
        await Expect(row).ToBeVisibleAsync();
        await Expect(page.Locator("#ActiveTokensTab")).ToHaveTextAsync("Active (1)");
        await Expect(page.Locator("#InactiveTokensTab")).ToHaveTextAsync("Inactive (0)");

        await modal.GetByRole(AriaRole.Button, new() { Name = "Copy token", Exact = true }).ClickAsync();
        Assert.True(string.Equals(token, await page.EvaluateAsync<string>("() => navigator.clipboard.readText()"), StringComparison.Ordinal),
            "Copy token should place the issued credential on the clipboard.");

        // Reload directly from the success step: this must be a GET, never another creation POST.
        var reload = await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        Assert.Equal("GET", reload!.Request.Method);
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_access_tokens"));
        await Expect(page.Locator("#IssuedToken")).ToHaveCountAsync(0);
        Assert.False((await page.ContentAsync()).Contains(token, StringComparison.Ordinal), "Reload must not redisplay the issued credential.");
        await Expect(row).ToBeVisibleAsync();
        await Expect(row.Locator("[data-token-name]")).ToContainTextAsync(name);
        await Expect(row.Locator("[data-token-status]")).ToHaveTextAsync("Active");
        await Expect(row).ToContainTextAsync("Never");

        using var agent = tester.Server.CreateHttpClient();
        agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using (var response = await agent.GetAsync("/api/v1/admin/me"))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(row).Not.ToContainTextAsync("Never");
        await Expect(page.GetByRole(AriaRole.Link, new() { Name = "View activity", Exact = true })).ToHaveCountAsync(0);

        // Create a second token with the custom lifetime, then age only this test database's row.
        await page.Locator("#CreateToken").ClickAsync();
        await modal.Locator("#Creation_Name").FillAsync("Expired UI agent");
        await modal.Locator("#TokenLifetime").SelectOptionAsync("custom");
        await Expect(modal.Locator("#Creation_ExpiresInDays")).ToBeVisibleAsync();
        await modal.Locator("#Creation_ExpiresInDays").FillAsync("12");
        await modal.Locator("#CreateTokenForm").GetByRole(AriaRole.Button, new() { Name = "Create token", Exact = true }).ClickAsync();
        await Expect(modal.Locator("#TokenSuccess")).ToBeVisibleAsync();
        await Expect(page.Locator("#ActiveTokensTab")).ToHaveTextAsync("Active (2)");
        var expiredId = await conn.ExecuteScalarAsync<Guid>("SELECT id FROM admin_access_tokens WHERE name = 'Expired UI agent'");
        var customExpiry = await conn.ExecuteScalarAsync<DateTime>("SELECT expires_at FROM admin_access_tokens WHERE id = @expiredId", new { expiredId });
        Assert.InRange((customExpiry - DateTime.UtcNow).TotalDays, 11.9, 12.1);
        await modal.GetByRole(AriaRole.Button, new() { Name = "Done", Exact = true }).ClickAsync();
        await Expect(modal).ToBeHiddenAsync();
        await Expect(page.Locator("#IssuedToken")).ToHaveCountAsync(0);
        await conn.ExecuteAsync("UPDATE admin_access_tokens SET expires_at = CURRENT_TIMESTAMP - INTERVAL '1 minute' WHERE id = @expiredId", new { expiredId });
        await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        var expiredRow = page.Locator($"tr[data-token-id='{expiredId}']");
        await Expect(row).ToBeVisibleAsync();
        await Expect(expiredRow).ToBeHiddenAsync();
        await page.Locator("#InactiveTokensTab").ClickAsync();
        await Expect(row).ToBeHiddenAsync();
        await Expect(expiredRow).ToBeVisibleAsync();
        await Expect(expiredRow.Locator("[data-token-status]")).ToHaveTextAsync("Expired");
        await page.Locator("#ActiveTokensTab").ClickAsync();

        await row.GetByRole(AriaRole.Button, new() { Name = "Revoke", Exact = true }).ClickAsync();
        await Expect(page.Locator("#ConfirmModal")).ToBeVisibleAsync();
        await Expect(page.Locator("#ConfirmTitle")).ToContainTextAsync(name);
        await Expect(page.Locator("#ConfirmModal review")).ToHaveCountAsync(0);
        await page.Locator("#ConfirmCancel").ClickAsync();
        await Expect(page.Locator("#ConfirmModal")).ToBeHiddenAsync();
        using (var response = await agent.GetAsync("/api/v1/admin/me"))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await row.GetByRole(AriaRole.Button, new() { Name = "Revoke", Exact = true }).ClickAsync();
        await page.Locator("#ConfirmContinue").ClickAsync();
        await Expect(page.Locator("#ConfirmModal")).ToBeHiddenAsync();
        await page.Locator("#InactiveTokensTab").ClickAsync();
        await Expect(row).ToBeVisibleAsync();
        await Expect(row.Locator("[data-token-status]")).ToHaveTextAsync("Revoked");
        await Expect(expiredRow.Locator("[data-token-status]")).ToHaveTextAsync("Expired");
        using (var response = await agent.GetAsync("/api/v1/admin/me"))
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(2, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_access_tokens"));
    }

    [Fact]
    public async Task CreationShowsServerValidationAndClipboardFallbackWithoutRestoringAClosedSecret()
    {
        await using var tester = CreateTester("AdminTokenClipboardUi");
        await tester.StartAsync();
        await tester.LogIn(await tester.CreateServerAdminAsync());
        await tester.GoToUrl("/account/admin-access-tokens");
        var page = tester.Page!;
        var pageErrors = new ConcurrentQueue<string>();
        page.PageError += (_, message) => pageErrors.Enqueue(message);
        await using var conn = await tester.Server.GetService<DBConnectionFactory>().Open();
        await page.Locator("#CreateToken").ClickAsync();
        var modal = page.Locator("#CreateTokenModal");
        var create = modal.Locator("#CreateTokenForm").GetByRole(AriaRole.Button, new() { Name = "Create token", Exact = true });

        // HTML required accepts whitespace; the real MVC Required validator must reject it.
        await modal.Locator("#Creation_Name").FillAsync("   ");
        var invalid = await page.RunAndWaitForResponseAsync(() => create.ClickAsync(), response =>
            response.Request.Method == "POST" && new Uri(response.Url).AbsolutePath == "/account/admin-access-tokens");
        Assert.Equal(200, invalid.Status);
        Assert.False(invalid.Request.IsNavigationRequest, "Server validation should appear in the open modal without navigation.");
        await Expect(modal.Locator("#TokenValidation li")).ToHaveCountAsync(1);
        await Expect(modal.Locator("#Creation_Name")).ToHaveValueAsync("   ");
        await Expect(create).ToBeEnabledAsync();
        await Expect(modal.Locator("#TokenSuccess")).ToBeHiddenAsync();
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_access_tokens"));
        await modal.Locator("#Creation_Name").FillAsync("Clipboard fallback agent");
        await create.ClickAsync();
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
        Assert.True(string.Equals(token, await modal.Locator("#SecretCopyText").InputValueAsync(), StringComparison.Ordinal),
            "Manual copy should provide the same issued credential when clipboard access is denied.");

        // Resolve an asynchronous clipboard write only after Done removes both secret fields.
        await page.EvaluateAsync("""
            () => Object.defineProperty(navigator, 'clipboard', {
                configurable: true,
                value: { writeText: () => new Promise(resolve => { window.finishTokenCopy = resolve; }) }
            })
            """);
        await modal.GetByRole(AriaRole.Button, new() { Name = "Copy token", Exact = true }).ClickAsync();
        await modal.GetByRole(AriaRole.Button, new() { Name = "Done", Exact = true }).ClickAsync();
        await Expect(modal).ToBeHiddenAsync();
        await Expect(page.Locator("#IssuedToken, #SecretCopyText")).ToHaveCountAsync(0);
        await page.EvaluateAsync("""
            async () => {
                window.finishTokenCopy();
                delete window.finishTokenCopy;
                await new Promise(resolve => setTimeout(resolve, 0));
            }
            """);
        Assert.Empty(pageErrors);
        Assert.False((await page.ContentAsync()).Contains(token, StringComparison.Ordinal), "Closing the modal must remove all displayed secret text.");
        await page.Locator("#CreateToken").ClickAsync();
        await Expect(modal.Locator("#CreateTokenForm")).ToBeVisibleAsync();
        await Expect(modal.Locator("#Creation_Name")).ToHaveValueAsync("");
        await Expect(modal.Locator("#TokenSuccess")).ToBeHiddenAsync();
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_access_tokens"));
    }

    [Fact]
    public async Task PendingCreationKeepsTheModalOpenAndDoesNotSubmitTwice()
    {
        await using var tester = CreateTester("AdminTokenPendingUi");
        await tester.StartAsync();
        await tester.LogIn(await tester.CreateServerAdminAsync());
        const string tokenPath = "/account/admin-access-tokens";
        await tester.GoToUrl(tokenPath);
        var page = tester.Page!;
        await using var conn = await tester.Server.GetService<DBConnectionFactory>().Open();
        await page.Locator("#CreateToken").ClickAsync();
        var modal = page.Locator("#CreateTokenModal");
        var form = modal.Locator("#CreateTokenForm");
        var create = form.Locator("button[type='submit']");
        await modal.Locator("#Creation_Name").FillAsync("Pending UI agent");

        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var creationRequests = 0;
        await page.RouteAsync("**" + tokenPath, async route =>
        {
            if (route.Request.Method == "POST")
            {
                Interlocked.Increment(ref creationRequests);
                received.TrySetResult();
                await release.Task;
            }
            await route.ContinueAsync();
        });

        var response = await page.RunAndWaitForResponseAsync(async () =>
        {
            try
            {
                await create.ClickAsync();
                await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await Expect(create).ToBeDisabledAsync();
                await page.Keyboard.PressAsync("Escape");
                Assert.True(await modal.EvaluateAsync<bool>("element => element.classList.contains('show')"),
                    "Escape must not dismiss the modal while token creation is pending.");
                await form.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
                Assert.True(await modal.EvaluateAsync<bool>("element => element.classList.contains('show')"),
                    "Cancel must not dismiss the modal while token creation is pending.");
                await form.EvaluateAsync("element => element.requestSubmit()");
            }
            finally
            {
                release.TrySetResult();
            }
        }, result => result.Request.Method == "POST" && new Uri(result.Url).AbsolutePath == tokenPath);

        Assert.Equal(200, response.Status);
        Assert.False(response.Request.IsNavigationRequest);
        await Expect(modal.Locator("#TokenSuccess")).ToBeVisibleAsync();
        Assert.Equal(1, creationRequests);
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_access_tokens"));
        await modal.GetByRole(AriaRole.Button, new() { Name = "Done", Exact = true }).ClickAsync();
        await Expect(modal).ToBeHiddenAsync();
        await page.Locator("#CreateToken").ClickAsync();
        await Expect(create).ToBeEnabledAsync();
        await Expect(modal.Locator("#Creation_Name")).ToHaveValueAsync("");
    }

    private PlaywrightTester CreateTester(string name)
    {
        var tester = new PlaywrightTester(Log, Create(name));
        tester.Server.ReuseDatabase = false;
        tester.Server.ConfigureServices = services =>
        {
            foreach (var descriptor in services.Where(descriptor =>
                         descriptor.ServiceType == typeof(IHostedService) &&
                         (descriptor.ImplementationType == typeof(DockerStartupHostedService) ||
                          descriptor.ImplementationType == typeof(AzureStartupHostedService))).ToArray())
                services.Remove(descriptor);
        };
        return tester;
    }
}
