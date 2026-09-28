using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;
using PluginBuilder.BuildBroker.HostedServices;
using PluginBuilder.HostedServices;
using PluginBuilder.Services;
using Xunit;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace PluginBuilder.Tests.AdminTests;

[Collection("Playwright Tests")]
public class AdminEventSubscriptionsUITests(ITestOutputHelper output) : UnitTestBase(output)
{
    [Fact]
    public async Task AdminCanCreateWebhookSeeSecretOnceThenDisableAndDelete()
    {
        await using var tester = CreateTester("EventSubscriptionsUi");
        await tester.StartAsync();
        var email = await tester.CreateServerAdminAsync();
        await tester.LogIn(email);
        var page = tester.Page!;
        await using var conn = await tester.Server.GetService<DBConnectionFactory>().Open();
        const string path = "/admin/event-subscriptions";
        const string destination = "https://review.example.com/hooks/plugin-builder";

        await tester.GoToUrl(path);
        await Expect(page.Locator("#AdminNav-EventSubscriptions")).ToHaveClassAsync(new Regex("active"));
        await Expect(page.Locator("#SubscriptionList")).ToContainTextAsync("No subscriptions yet.");

        // An invalid destination is rejected by the same validation as the API, and nothing is stored.
        await page.Locator("#CreateSubscription").ClickAsync();
        var modal = page.Locator("#CreateSubscriptionModal");
        await Expect(modal).ToBeVisibleAsync();
        await modal.Locator("#Creation_Destination").FillAsync("http://review.example.com/hooks");
        await modal.Locator("#CreateSubscriptionForm").GetByRole(AriaRole.Button, new() { Name = "Create subscription", Exact = true }).ClickAsync();
        await Expect(modal.Locator("#SubscriptionValidation")).ToContainTextAsync("Webhook destination must be an HTTPS URL");
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_event_subscriptions"));

        await modal.Locator("#Creation_Destination").FillAsync(destination);
        await modal.Locator("#EventType-build-succeeded").CheckAsync();
        await modal.Locator("#EventType-listing-requested").CheckAsync();
        var creation = await page.RunAndWaitForResponseAsync(
            () => modal.Locator("#CreateSubscriptionForm").GetByRole(AriaRole.Button, new() { Name = "Create subscription", Exact = true }).ClickAsync(),
            response => response.Request.Method == "POST" && new Uri(response.Url).AbsolutePath == path);
        Assert.False(creation.Request.IsNavigationRequest, "Creation should keep the current page and modal open.");
        Assert.Equal(200, creation.Status);
        await Expect(modal.Locator("#SubscriptionSuccess")).ToBeVisibleAsync();
        var secret = await modal.Locator("#IssuedSecret").InputValueAsync();
        Assert.Equal(32, Convert.FromBase64String(secret).Length);

        var row = await conn.QuerySingleAsync<(Guid Id, string Kind, string Destination, string[] EventTypes, bool Enabled, string ProtectedSecret)>(
            "SELECT id, kind, destination, event_types, enabled, protected_secret FROM admin_event_subscriptions");
        Assert.Equal("webhook", row.Kind);
        Assert.Equal(destination, row.Destination);
        Assert.Equal(new[] { "build.succeeded", "listing.requested" }, row.EventTypes.OrderBy(t => t, StringComparer.Ordinal));
        Assert.True(row.Enabled);
        // The secret shown is the one deliveries are signed with.
        var protector = tester.Server.GetService<IDataProtectionProvider>().CreateProtector(AdminEventSubscriptionService.SecretPurpose);
        Assert.Equal(secret, protector.Unprotect(row.ProtectedSecret));
        await Expect(modal.Locator("[data-issued-id]")).ToHaveTextAsync(row.Id.ToString());

        var listed = page.Locator($"tr[data-subscription-id='{row.Id}']");
        await Expect(listed).ToBeVisibleAsync();
        await Expect(listed.Locator("[data-subscription-destination]")).ToHaveTextAsync(destination);
        await Expect(listed.Locator("[data-subscription-status]")).ToHaveTextAsync("Enabled");

        // Reloading from the success step is a GET: it never creates a second subscription or shows the secret again.
        var reload = await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        Assert.Equal("GET", reload!.Request.Method);
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_event_subscriptions"));
        await Expect(page.Locator("#IssuedSecret")).ToHaveCountAsync(0);
        Assert.False((await page.ContentAsync()).Contains(secret, StringComparison.Ordinal), "Reload must not redisplay the secret.");

        await listed.GetByRole(AriaRole.Button, new() { Name = "Disable", Exact = true }).ClickAsync();
        await Expect(listed.Locator("[data-subscription-status]")).ToHaveTextAsync("Disabled");
        Assert.False(await conn.ExecuteScalarAsync<bool>("SELECT enabled FROM admin_event_subscriptions WHERE id = @Id", new { row.Id }));
        await listed.GetByRole(AriaRole.Button, new() { Name = "Enable", Exact = true }).ClickAsync();
        await Expect(listed.Locator("[data-subscription-status]")).ToHaveTextAsync("Enabled");

        await listed.GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();
        await Expect(page.Locator("#ConfirmModal")).ToBeVisibleAsync();
        await page.Locator("#ConfirmContinue").ClickAsync();
        await Expect(listed).ToHaveCountAsync(0);
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_event_subscriptions"));
    }

    [Fact]
    public async Task EmailSubscriptionIsCreatedWithoutASecret()
    {
        await using var tester = CreateTester("EventSubscriptionsEmailUi");
        await tester.StartAsync();
        await tester.LogIn(await tester.CreateServerAdminAsync());
        var page = tester.Page!;
        await using var conn = await tester.Server.GetService<DBConnectionFactory>().Open();

        await tester.GoToUrl("/admin/event-subscriptions");
        await page.Locator("#CreateSubscription").ClickAsync();
        var modal = page.Locator("#CreateSubscriptionModal");
        await modal.Locator("#Creation_Kind").SelectOptionAsync("email");
        await modal.Locator("#Creation_Destination").FillAsync("reviews@example.com");
        await modal.Locator("#CreateSubscriptionForm").GetByRole(AriaRole.Button, new() { Name = "Create subscription", Exact = true }).ClickAsync();
        await Expect(modal.Locator("#SubscriptionSuccess")).ToContainTextAsync("Email subscriptions have no signing secret.");
        await Expect(modal.Locator("#IssuedSecret")).ToHaveCountAsync(0);
        var row = await conn.QuerySingleAsync<(string Kind, string[] EventTypes, string? ProtectedSecret)>(
            "SELECT kind, event_types, protected_secret FROM admin_event_subscriptions");
        Assert.Equal("email", row.Kind);
        Assert.Empty(row.EventTypes);
        Assert.Null(row.ProtectedSecret);
    }

    [Fact]
    public async Task NonAdminsAreDeniedAndPostsWithoutAntiforgeryTokenAreRejected()
    {
        await using var tester = CreateTester("EventSubscriptionsDeniedUi");
        await tester.StartAsync();
        var page = tester.Page!;
        await using var conn = await tester.Server.GetService<DBConnectionFactory>().Open();
        var url = new Uri(tester.ServerUri!, "/admin/event-subscriptions").ToString();
        var noRedirects = new APIRequestContextOptions { MaxRedirects = 0 };
        static string Csrf(string html) =>
            WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);

        // A signed-in account without the ServerAdmin role is sent to the access-denied page, for the page and for creation.
        var userEmail = $"user-{Guid.NewGuid():N}@test.com";
        await tester.Server.CreateFakeUserAsync(userEmail);
        await tester.LogIn(userEmail);
        var get = await page.Context.APIRequest.GetAsync(url, noRedirects);
        Assert.Equal(302, get.Status);
        Assert.Contains("/errors/403", get.Headers["location"], StringComparison.Ordinal);

        // Send the user's own valid antiforgery token, so the role check, not the token, is what refuses the post.
        await tester.GoToUrl("/dashboard");
        var userToken = Csrf(await page.ContentAsync());
        Assert.False(string.IsNullOrEmpty(userToken), "The dashboard should render an antiforgery token for the signed-in user.");
        var form = page.Context.APIRequest.CreateFormData();
        form.Set("__RequestVerificationToken", userToken);
        form.Set("Creation.Kind", "webhook");
        form.Set("Creation.Destination", "https://review.example.com/hooks/plugin-builder");
        var userPost = await page.Context.APIRequest.PostAsync(url, new APIRequestContextOptions { Form = form, MaxRedirects = 0 });
        Assert.Equal(302, userPost.Status);
        Assert.Contains("/errors/403", userPost.Headers["location"], StringComparison.Ordinal);
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_event_subscriptions"));

        // An admin post without the antiforgery token is rejected before anything is written.
        await tester.Logout();
        await tester.LogIn(await tester.CreateServerAdminAsync());
        var tokenless = page.Context.APIRequest.CreateFormData();
        tokenless.Set("Creation.Kind", "webhook");
        tokenless.Set("Creation.Destination", "https://review.example.com/hooks/plugin-builder");
        var adminPost = await page.Context.APIRequest.PostAsync(url, new APIRequestContextOptions { Form = tokenless, MaxRedirects = 0 });
        Assert.Equal(400, adminPost.Status);
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_event_subscriptions"));
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
