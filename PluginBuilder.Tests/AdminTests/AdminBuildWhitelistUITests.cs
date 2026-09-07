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
public class AdminBuildWhitelistUITests(ITestOutputHelper output) : UnitTestBase(output)
{
    [Fact]
    public async Task SettingsEditor_DeleteSendsVersionAndReloadsWhileStaleDeleteShowsConflict()
    {
        await using var tester = new PlaywrightTester(Log, Create("BuildWhitelistEditorUi"));
        tester.Server.ReuseDatabase = false;
        tester.Server.ConfigureServices = services =>
        {
            foreach (var descriptor in services.Where(descriptor =>
                         descriptor.ServiceType == typeof(IHostedService) &&
                         (descriptor.ImplementationType == typeof(DockerStartupHostedService) ||
                          descriptor.ImplementationType == typeof(AzureStartupHostedService))).ToArray())
                services.Remove(descriptor);
        };
        await tester.StartAsync();
        await tester.LogIn(await tester.CreateServerAdminAsync());
        var email = $"trusted-{Guid.NewGuid():N}@example.test";
        var userId = await tester.CreateConfirmedUser(email);
        await using var conn = await tester.Server.GetService<DBConnectionFactory>().Open();

        var page = tester.Page!;
        await page.GotoAsync(new Uri(tester.ServerUri!, "/admin/SettingsEditor").ToString(),
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        var row = page.Locator("tr[data-key='NewBuildsWhitelist']");
        await Expect(row).ToHaveCountAsync(1);
        await Expect(row.Locator(".value")).ToHaveTextAsync("");

        // Keep a second tab's original version while saving a grant through the real editor.
        var stalePage = await page.Context.NewPageAsync();
        stalePage.SetDefaultTimeout(10_000);
        try
        {
            await stalePage.GotoAsync(page.Url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await row.GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true }).ClickAsync();
            await row.Locator("textarea").FillAsync(email);
            await row.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
            await Expect(row.Locator(".value")).ToHaveTextAsync(email);
            Assert.Equal(new[] { userId }, (await conn.QueryAsync<string>("SELECT user_id FROM build_whitelist")).ToArray());

            var dialogReceived = new TaskCompletionSource<IDialog>(TaskCreationOptions.RunContinuationsAsynchronously);
            stalePage.Dialog += (_, dialog) => dialogReceived.TrySetResult(dialog);
            var staleDelete = stalePage.RunAndWaitForResponseAsync(
                () => stalePage.Locator("tr[data-key='NewBuildsWhitelist']")
                    .GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync(),
                response => IsEditorResponse(response, "DELETE"));
            var dialog = await dialogReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var message = dialog.Message;
            await dialog.DismissAsync();
            Assert.Equal(409, (await staleDelete).Status);
            Assert.Contains("Reload the page", message, StringComparison.Ordinal);
            Assert.Equal(new[] { userId }, (await conn.QueryAsync<string>("SELECT user_id FROM build_whitelist")).ToArray());

            // Observe the actual DELETE and the navigation triggered by its success handler.
            // A manual navigation here would hide a missing version or missing reload in the UI.
            var deleted = page.WaitForResponseAsync(response => IsEditorResponse(response, "DELETE"));
            var reload = await page.RunAndWaitForResponseAsync(
                () => row.GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync(),
                response => response.Request.IsNavigationRequest && IsEditorResponse(response, "GET"));
            Assert.Equal(200, (await deleted).Status);
            Assert.Equal(200, reload.Status);
            await Expect(row).ToHaveCountAsync(1);
            await Expect(row.Locator(".value")).ToHaveTextAsync("");
            Assert.Empty(await conn.QueryAsync<string>("SELECT user_id FROM build_whitelist"));
        }
        finally
        {
            await stalePage.CloseAsync();
        }
    }

    private static bool IsEditorResponse(IResponse response, string method) =>
        response.Request.Method == method && new Uri(response.Url).AbsolutePath == "/admin/SettingsEditor";
}
