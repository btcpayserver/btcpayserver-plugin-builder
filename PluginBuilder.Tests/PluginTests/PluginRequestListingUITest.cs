using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright.Xunit;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.DataModels;
using PluginBuilder.Services;
using PluginBuilder.Util;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.PluginTests;

[Collection("Playwright Tests")]
public class PluginRequestListingUITest(ITestOutputHelper output) : PageTest
{
    private readonly XUnitLogger _log = new("PluginRequestListingUITest", output);



    [Fact]
    public async Task RequestListing_Tests()
    {
        await using var t = new PlaywrightTester(_log);
        t.Server.ReuseDatabase = false;
        await t.StartAsync();
        await using var conn = await t.Server.GetService<DBConnectionFactory>().Open();
        await conn.SettingsSetAsync(SettingsKeys.VerifiedGithub, "true");
        var verfCache = t.Server.GetService<AdminSettingsCache>();
        await verfCache.RefreshAllAdminSettings(conn);

        await t.GoToUrl("/register");
        var user = await t.RegisterNewUser();
        await Expect(t.Page!).ToHaveURLAsync(new Regex(".*/dashboard$", RegexOptions.IgnoreCase));
        await t.VerifyUserAccounts(user);

        var pluginSlug = "cb-a-" + PlaywrightTester.GetRandomUInt256()[..8];
        await t.GoToUrl("/plugins/create");
        await t.Page.FillAsync("#PluginSlug", pluginSlug);
        await t.Page!.FillAsync("#PluginTitle", pluginSlug);
        await t.Page!.FillAsync("#Description", "Test");
        await t.Page.ClickAsync("#Create");
        await t.AssertNoError();

        await t.GoToUrl($"/plugins/{pluginSlug}");
        await t.Page.ClickAsync("#CreateNewBuild");
        await t.Page.FillAsync("#GitRepository", ServerTester.RepoUrl);
        await t.Page.FillAsync("#GitRef", ServerTester.GitRef);
        await t.Page.FillAsync("#PluginDirectory", ServerTester.PluginDir);
        await t.Page.FillAsync("#BuildConfig", ServerTester.BuildCfg);
        await t.Page.ClickAsync("#Create");
        await Expect(t.Page).ToHaveURLAsync(new Regex($@"/plugins/{Regex.Escape(pluginSlug)}/builds/\d+$", RegexOptions.IgnoreCase));
        var m = Regex.Match(t.Page.Url, @"/builds/(\d+)$");
        Assert.True(m.Success, "Could not parse build url");
        var buildIdA = int.Parse(m.Groups[1].Value);
        var terminal = await t.Server.WaitForBuildToFinishAsync(new FullBuildId(pluginSlug, buildIdA));
        Assert.Equal(BuildStates.Uploaded, terminal);
        await Task.Delay(2_000);
        await t.Page.ReloadAsync();
        await Expect(t.Page!.Locator("button:text-is('Release')")).ToBeVisibleAsync();
        await t.Page.ClickAsync("button:text-is('Release')");

        await t.Page!.ClickAsync("#StoreNav-Dashboard");
        await t.Page.ClickAsync("a.btn.btn-primary:text('Request Listing')");
        await Expect(t.Page.Locator("#collapsePluginSettings")).ToBeVisibleAsync();
        await Expect(t.Page.Locator("#pluginSettingsHeader")).ToContainTextAsync("Update Plugin Settings");
        await Expect(t.Page.Locator("#collapseOwnerSettings")).Not.ToBeVisibleAsync();
        await Expect(t.Page.Locator("#collapseRequestForm")).Not.ToBeVisibleAsync();
        await t.Page!.ClickAsync("#StoreNav-Settings");

        var testImagePath = Path.Combine(Path.GetTempPath(), "test-logo2.png");
        t.CreateTestImage(testImagePath);
        await t.Page.FillAsync("#PluginTitle", "Logo Test Plugin Updated");
        await t.Page.FillAsync("#Description", "Testing logo upload with description");
        await t.Page.FillAsync("#GitRepository", "https://github.com/test/repo");
        await t.Page.FillAsync("#Documentation", "https://btcpayserver.org/");
        var fileInput = t.Page.Locator("input[type='file'][name='Logo']");
        await fileInput.SetInputFilesAsync(testImagePath);
        await t.Page.Locator("button[type='submit'][form='plugin-setting-form']").ClickAsync();
        await t.AssertNoError();

        await t.Page!.ClickAsync("#StoreNav-Dashboard");
        await t.Page.ClickAsync("a.btn.btn-primary:text('Request Listing')");
        await Expect(t.Page.Locator("#collapseRequestForm")).ToBeVisibleAsync();
        await Expect(t.Page.Locator("#collapsePluginSettings")).Not.ToBeVisibleAsync();
        await Expect(t.Page.Locator("#collapseOwnerSettings")).Not.ToBeVisibleAsync();
        await t.Page.FillAsync("textarea[name='ReleaseNote']", "Testing release note entry");
        await t.Page.FillAsync("input[name='TelegramVerificationMessage']", "https://t.me/btcpayserver/1234");
        await t.Page.FillAsync("textarea[name='UserReviews']", "Great plugin, works as expected!");
        await t.Page.ClickAsync("button[type='submit']:text('Submit')");
        await t.AssertNoError();
        await t.Page.ClickAsync("a.btn.btn-primary:text('Request Listing')");

        await Expect(t.Page.Locator("#collapsePluginSettings")).Not.ToBeVisibleAsync();
        await Expect(t.Page.Locator("#collapseOwnerSettings")).Not.ToBeVisibleAsync();
        await Expect(t.Page.Locator("#collapseRequestForm")).Not.ToBeVisibleAsync();
        await Expect(t.Page.Locator(".alert-warning")).ToContainTextAsync("Your listing request has been sent and is pending validation");

        // Verify the listing request was created in the database
        var pendingRequest = await conn.GetPendingOrRejectedListingRequestForPlugin(pluginSlug);
        Assert.NotNull(pendingRequest);
        Assert.Equal("Testing release note entry", pendingRequest.ReleaseNote);
        Assert.Equal("https://t.me/btcpayserver/1234", pendingRequest.TelegramVerificationMessage);
        Assert.Equal("Great plugin, works as expected!", pendingRequest.UserReviews);
        Assert.Equal(PluginListingRequestStatus.Pending, pendingRequest.Status);

        // Simulate admin approval by setting plugin to listed
        await conn.SetPluginSettings(pluginSlug, null, PluginVisibilityEnum.Listed);
        await t.Page!.ClickAsync("#StoreNav-Dashboard");
        var buttonCount = await t.Page.Locator("a.btn.btn-primary:has-text('Request Listing')").CountAsync();
        Assert.Equal(0, buttonCount);
    }

    [Fact]
    public async Task Admin_Can_View_And_Approve_ListingRequest()
    {
        await using var t = new PlaywrightTester(_log);
        t.Server.ReuseDatabase = false;
        await t.StartAsync();
        await using var conn = await t.Server.GetService<DBConnectionFactory>().Open();

        // Create a plugin with a pending listing request
        var pluginSlug = "test-plugin-" + PlaywrightTester.GetRandomUInt256()[..8];
        var userId = await t.Server.CreateFakeUserAsync();
        var fullBuildId = await t.Server.CreateAndBuildPluginAsync(userId, pluginSlug);

        // Create a listing request
        var requestId = await conn.CreateListingRequest(
            pluginSlug,
            "This is a test plugin for approval",
            "https://t.me/btcpayserver/12345",
            "https://example.com/review",
            null
        );

        // Create admin user and login
        var adminEmail = await CreateServerAdminAsync(t);
        await t.LogIn(adminEmail);

        // Navigate to listing requests page
        await t.GoToUrl("/admin/listing-requests");
        await Expect(t.Page!).ToHaveURLAsync(new Regex(".*/admin/listing-requests", RegexOptions.IgnoreCase));

        // Verify the request is visible in the list - use table row to avoid ambiguity
        var row = t.Page.Locator($"tbody tr:has-text('{pluginSlug}')");
        await Expect(row).ToBeVisibleAsync();
        await Expect(row.Locator(".badge.bg-warning:text('Pending')")).ToBeVisibleAsync();

        // Click to view details
        await t.Page.ClickAsync("a.btn.btn-sm.btn-primary:text('View Details')");
        await Expect(t.Page).ToHaveURLAsync(new Regex($".*/admin/listing-requests/{requestId}", RegexOptions.IgnoreCase));

        // Verify request details are displayed
        await Expect(t.Page.Locator("text=This is a test plugin for approval")).ToBeVisibleAsync();
        await Expect(t.Page.Locator("text=https://t.me/btcpayserver/12345")).ToBeVisibleAsync();
        await Expect(t.Page.Locator("text=https://example.com/review")).ToBeVisibleAsync();

        // Approve the request
        await t.Page.ClickAsync("button.btn.btn-success:text('Approve')");
        await t.Page.ClickAsync("button[type='submit'].btn.btn-success:text('Approve')"); // Confirm in modal

        // Should redirect back to listing requests page
        await Expect(t.Page).ToHaveURLAsync(new Regex(".*/admin/listing-requests$", RegexOptions.IgnoreCase));

        // Verify the request is no longer pending
        var approvedRequest = await conn.GetListingRequest(requestId);
        Assert.NotNull(approvedRequest);
        Assert.Equal(PluginListingRequestStatus.Approved, approvedRequest.Status);
        Assert.NotNull(approvedRequest.ReviewedAt);
        Assert.NotNull(approvedRequest.ReviewedBy);

        // Verify plugin visibility was updated to listed
        var plugin = await conn.GetPluginDetails(pluginSlug);
        Assert.Equal(PluginVisibilityEnum.Listed, plugin!.Visibility);
    }

    [Fact]
    public async Task Admin_Can_Reject_ListingRequest()
    {
        await using var t = new PlaywrightTester(_log);
        t.Server.ReuseDatabase = false;
        await t.StartAsync();
        await using var conn = await t.Server.GetService<DBConnectionFactory>().Open();

        // Create a plugin with a pending listing request
        var pluginSlug = "test-plugin-reject-" + PlaywrightTester.GetRandomUInt256()[..8];
        var userId = await t.Server.CreateFakeUserAsync();
        await t.Server.CreateAndBuildPluginAsync(userId, pluginSlug);

        var requestId = await conn.CreateListingRequest(
            pluginSlug,
            "Test plugin for rejection",
            "https://t.me/btcpayserver/99999",
            "https://example.com/bad-review",
            null
        );

        // Create admin user and login
        var adminEmail = await CreateServerAdminAsync(t);
        await t.LogIn(adminEmail);

        // Navigate to request detail
        await t.GoToUrl($"/admin/listing-requests/{requestId}");

        // Reject the request
        await t.Page.ClickAsync("button.btn.btn-danger:text('Reject')");
        await t.Page.FillAsync("textarea#rejectionReason", "Plugin does not meet quality standards");
        await t.Page.ClickAsync("button[type='submit'].btn.btn-danger:text('Reject')");

        // Should redirect back to listing requests page
        await Expect(t.Page).ToHaveURLAsync(new Regex(".*/admin/listing-requests$", RegexOptions.IgnoreCase));

        // Verify the request was rejected
        var rejectedRequest = await conn.GetListingRequest(requestId);
        Assert.NotNull(rejectedRequest);
        Assert.Equal(PluginListingRequestStatus.Rejected, rejectedRequest.Status);
        Assert.Equal("Plugin does not meet quality standards", rejectedRequest.RejectionReason);
        Assert.NotNull(rejectedRequest.ReviewedAt);
        Assert.NotNull(rejectedRequest.ReviewedBy);

        // Verify plugin visibility was NOT changed
        var plugin = await conn.GetPluginDetails(pluginSlug);
        Assert.Equal(PluginVisibilityEnum.Unlisted, plugin!.Visibility);
    }

    private static async Task<string> CreateServerAdminAsync(PlaywrightTester tester)
    {
        using var scope = tester.Server.WebApp.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

        if (!await roleManager.RoleExistsAsync(Roles.ServerAdmin))
        {
            await roleManager.CreateAsync(new IdentityRole(Roles.ServerAdmin));
        }

        var email = $"admin-{Guid.NewGuid():N}@test.com";
        const string password = "123456";
        var user = new IdentityUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true
        };

        var result = await userManager.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            throw new InvalidOperationException($"Failed to create admin user: {errors}");
        }

        await userManager.AddToRoleAsync(user, Roles.ServerAdmin);
        return email;
    }
}

