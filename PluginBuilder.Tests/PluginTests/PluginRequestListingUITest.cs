using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.DataModels;
using PluginBuilder.Services;
using PluginBuilder.Util;
using PluginBuilder.Util.Extensions;
using PluginBuilder.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.PluginTests;

[Collection("Playwright Tests")]
public class PluginRequestListingUITest(ITestOutputHelper output) : PageTest
{
    private readonly XUnitLogger _log = new("PluginRequestListingUITest", output);

    [Theory]
    [InlineData("https://t.me/btcpayserver/1234", true)]
    [InlineData("http://t.me/btcpayserver/1234", false)]
    [InlineData("https://example.com/btcpayserver/1234", false)]
    [InlineData("https://t.me/not-btcpayserver/1234", false)]
    public void TelegramVerificationMessageValidation_MatchesServerContract(string value, bool expected)
    {
        Assert.Equal(expected, RequestListingViewModel.IsValidTelegramVerificationMessage(value));
    }

    [Fact]
    public async Task RequestListing_Tests()
    {
        await using var t = new PlaywrightTester(_log);
        t.Server.ReuseDatabase = false;
        await t.StartAsync();
        var connectionFactory = t.Server.GetService<DBConnectionFactory>();
        await using var conn = await connectionFactory.Open();
        await t.EnableGithubVerificationAsync(conn);

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
        await t.Page.ClickAsync("#StoreNav-RequestListing");
        await Expect(t.Page.Locator("#collapsePluginSettings")).ToBeVisibleAsync();
        await Expect(t.Page.Locator("#pluginSettingsHeader")).ToContainTextAsync("Update Plugin Settings");
        await Expect(t.Page.Locator("#collapseOwnerSettings")).Not.ToBeVisibleAsync();
        await Expect(t.Page.Locator("#collapseRequestForm")).Not.ToBeVisibleAsync();
        await Expect(t.Page.Locator("#SubmitListing")).ToBeDisabledAsync();
        var requestFormToggle = t.Page.Locator("#requestFormHeader button");
        await Expect(requestFormToggle).ToBeEnabledAsync();
        await requestFormToggle.ClickAsync();
        await Expect(t.Page.Locator("#collapseRequestForm")).ToBeVisibleAsync();
        await Expect(t.Page.Locator("#ReleaseNote")).ToBeEditableAsync();
        await Expect(t.Page.Locator("#TelegramVerificationMessage")).ToBeEditableAsync();
        await Expect(t.Page.Locator("#UserReviews")).ToBeEditableAsync();
        await Expect(t.Page.Locator("#AnnouncementDate")).ToBeEditableAsync();
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
        await t.Page.ClickAsync("#StoreNav-RequestListing");
        await Expect(t.Page.Locator("#plugin-settings-status")).ToContainTextAsync("Incomplete");
        await Expect(t.Page.Locator("#owner-settings-status")).ToContainTextAsync("Complete");
        await Expect(t.Page.Locator("#plugin-requirement-description")).ToContainTextAsync("Complete");
        await Expect(t.Page.Locator("#plugin-requirement-git-repository")).ToContainTextAsync("Complete");
        await Expect(t.Page.Locator("#plugin-requirement-documentation")).ToContainTextAsync("Complete");
        await Expect(t.Page.Locator("#plugin-requirement-video-url")).ToContainTextAsync("Missing");
        await Expect(t.Page.Locator("#plugin-requirement-logo")).ToContainTextAsync("Complete");
        await Expect(t.Page.Locator("#UpdatePluginSettingsLink")).ToBeVisibleAsync();
        await Expect(t.Page.Locator("#collapsePluginSettings")).ToBeVisibleAsync();
        await Expect(t.Page.Locator("#collapseOwnerSettings")).Not.ToBeVisibleAsync();
        await Expect(t.Page.Locator("#collapseRequestForm")).Not.ToBeVisibleAsync();
        await Expect(t.Page.Locator("#SubmitListing")).ToBeDisabledAsync();
        await Expect(requestFormToggle).ToBeEnabledAsync();
        await requestFormToggle.ClickAsync();
        await Expect(t.Page.Locator("#collapseRequestForm")).ToBeVisibleAsync();
        await Expect(t.Page.Locator("#ReleaseNote")).ToBeEditableAsync();
        await t.Page.FillAsync("#TelegramVerificationMessage", "https://t.me/btcpayserver/1234");
        await t.Page.FillAsync("#UserReviews", "https://example.com/review");
        await Expect(t.Page.Locator("#request-form-status")).ToContainTextAsync("Complete");
        await Expect(t.Page.Locator("#SubmitListing")).ToBeDisabledAsync();

        var incompleteSettingsResponse = await PostListingRequest(t.Page);
        Assert.True(incompleteSettingsResponse.Ok);
        Assert.Contains("Complete every required field in your plugin settings before submitting.", await incompleteSettingsResponse.TextAsync());
        Assert.Null(await conn.GetPendingListingRequestForPlugin(new PluginSlug(pluginSlug)));

        await t.Page.ClickAsync("#StoreNav-Settings");
        await t.Page.FillAsync("#VideoUrl", "https://www.youtube.com/watch?v=Z78ZbPcsc3g&t=1228s");
        await t.Page.Locator("button[type='submit'][form='plugin-setting-form']").ClickAsync();
        await t.AssertNoError();

        await t.Page.ClickAsync("#StoreNav-Dashboard");
        await t.Page.ClickAsync("#StoreNav-RequestListing");
        await Expect(t.Page.Locator("#plugin-settings-status")).ToContainTextAsync("Complete");
        await Expect(t.Page.Locator("#owner-settings-status")).ToContainTextAsync("Complete");
        await Expect(t.Page.Locator("#plugin-settings-status")).ToHaveClassAsync(new Regex("\\btext-black\\b"));
        await Expect(t.Page.Locator("#owner-settings-status")).ToHaveClassAsync(new Regex("\\btext-black\\b"));
        await Expect(t.Page.Locator("#collapseRequestForm")).ToBeVisibleAsync();
        await Expect(t.Page.Locator("#collapsePluginSettings")).Not.ToBeVisibleAsync();
        await Expect(t.Page.Locator("#collapseOwnerSettings")).Not.ToBeVisibleAsync();
        await Expect(requestFormToggle).ToBeEnabledAsync();
        await Expect(t.Page.Locator("#request-form-status")).ToContainTextAsync("Incomplete");
        await Expect(t.Page.Locator("#ReleaseNote")).ToHaveValueAsync("Testing logo upload with description");
        await Expect(t.Page.Locator("#ReleaseNote")).ToBeEditableAsync();
        await Expect(t.Page.Locator("#request-listing-form .badge")).ToHaveCountAsync(0);
        await Expect(t.Page.Locator("#request-listing-form")).Not.ToContainTextAsync("Missing");
        await Expect(t.Page.Locator("#SubmitListing")).ToBeDisabledAsync();
        var currentOwnerId = (await conn.GetPluginOwners(new PluginSlug(pluginSlug))).Single(owner => owner.Email == user).UserId;
        var currentOwner = t.Page.Locator($"[data-owner-id='{currentOwnerId}']");
        await Expect(currentOwner).ToContainTextAsync("You");
        var primaryBadge = currentOwner.Locator(".badge:text-is('Primary')");
        await Expect(primaryBadge).ToHaveClassAsync(new Regex("\\bbg-primary\\b"));
        await Expect(primaryBadge).ToHaveClassAsync(new Regex("\\btext-black\\b"));

        var currentOwnerAccount = await conn.GetAccountDetailSettings(currentOwnerId);
        Assert.NotNull(currentOwnerAccount);
        currentOwnerAccount.Nostr = null;
        await conn.SetAccountDetailSettings(currentOwnerAccount, currentOwnerId);
        await t.Page.ReloadAsync();

        await Expect(t.Page.Locator("#owner-settings-status")).ToContainTextAsync("Incomplete");
        await Expect(currentOwner.Locator("[data-verification='nostr']")).ToContainTextAsync("Not verified");
        await Expect(requestFormToggle).ToBeEnabledAsync();
        await Expect(t.Page.Locator("#ReleaseNote")).ToBeEditableAsync();
        var verifyMyAccounts = t.Page.Locator("a:text-is('Verify my accounts')");
        await Expect(verifyMyAccounts).ToBeVisibleAsync();
        await Expect(verifyMyAccounts).ToHaveAttributeAsync("href", "/account/details");
        await t.VerifyUserAccounts(user);

        var partialOwnerId = await t.Server.CreateFakeUserAsync(confirmEmail: true, githubVerified: true);
        await conn.AddUserPlugin(new PluginSlug(pluginSlug), partialOwnerId);
        await t.Page.ReloadAsync();

        await Expect(t.Page.Locator("#plugin-settings-status")).ToContainTextAsync("Complete");
        await Expect(t.Page.Locator("#owner-settings-status")).ToContainTextAsync("Incomplete");
        await Expect(t.Page.Locator("#collapseOwnerSettings")).ToBeVisibleAsync();
        await Expect(t.Page.Locator("#collapseRequestForm")).Not.ToBeVisibleAsync();
        await Expect(requestFormToggle).ToBeEnabledAsync();
        await Expect(t.Page.Locator("#ReleaseNote")).ToBeEditableAsync();
        var partialOwner = t.Page.Locator($"[data-owner-id='{partialOwnerId}']");
        await Expect(partialOwner).ToBeVisibleAsync();
        await Expect(partialOwner.Locator("[data-verification='email']")).ToContainTextAsync("Verified");
        await Expect(partialOwner.Locator("[data-verification='github']")).ToContainTextAsync("Verified");
        await Expect(partialOwner.Locator("[data-verification='nostr']")).ToContainTextAsync("Not verified");
        await Expect(t.Page.Locator("a:text-is('Verify my accounts')")).ToHaveCountAsync(0);

        var incompleteOwnersResponse = await PostListingRequest(t.Page);
        Assert.True(incompleteOwnersResponse.Ok);
        Assert.Contains("Every plugin owner must verify Email, GitHub, and Nostr before submitting.", await incompleteOwnersResponse.TextAsync());
        Assert.Null(await conn.GetPendingListingRequestForPlugin(new PluginSlug(pluginSlug)));

        await conn.RemovePluginOwner(new PluginSlug(pluginSlug), partialOwnerId);
        await t.Page.ReloadAsync();
        await Expect(t.Page.Locator("#owner-settings-status")).ToContainTextAsync("Complete");
        await Expect(t.Page.Locator("#collapseRequestForm")).ToBeVisibleAsync();
        await Expect(requestFormToggle).ToBeEnabledAsync();
        await Expect(t.Page.Locator("#ReleaseNote")).ToBeEditableAsync();

        await t.Page.FillAsync("#ReleaseNote", "Testing release note entry");
        await t.Page.FillAsync("#UserReviews", "https://example.com/review");
        var telegramInput = t.Page.Locator("#TelegramVerificationMessage");
        var invalidTelegramUrls = new[]
        {
            "http://t.me/btcpayserver/1234",
            "https://example.com/btcpayserver/1234",
            "https://t.me/not-btcpayserver/1234"
        };
        foreach (var invalidTelegramUrl in invalidTelegramUrls)
        {
            await telegramInput.FillAsync(invalidTelegramUrl);
            await Expect(telegramInput).ToHaveAttributeAsync("aria-invalid", "true");
            await Expect(telegramInput).ToHaveClassAsync(new Regex("\\bis-invalid\\b"));
            await Expect(t.Page.Locator("#telegram-verification-message-error")).ToBeVisibleAsync();
            await Expect(t.Page.Locator("#request-form-status")).ToContainTextAsync("Incomplete");
            await Expect(t.Page.Locator("#SubmitListing")).ToBeDisabledAsync();
        }

        await t.Page.FillAsync("#TelegramVerificationMessage", "https://t.me/btcpayserver/1234");
        await Expect(t.Page.Locator("#TelegramVerificationMessage")).ToHaveAttributeAsync("aria-invalid", "false");
        await Expect(t.Page.Locator("#telegram-verification-message-error")).Not.ToBeVisibleAsync();
        await Expect(t.Page.Locator("#request-form-status")).ToContainTextAsync("Complete");
        await Expect(t.Page.Locator("#request-form-status")).ToHaveClassAsync(new Regex("\\btext-black\\b"));
        await Expect(t.Page.Locator("#SubmitListing")).ToBeEnabledAsync();

        await t.Page.FillAsync("#ReleaseNote", "");
        await Expect(t.Page.Locator("#request-form-status")).ToContainTextAsync("Incomplete");
        await Expect(t.Page.Locator("#SubmitListing")).ToBeDisabledAsync();
        await t.Page.FillAsync("#ReleaseNote", "Testing release note entry");
        await Expect(t.Page.Locator("#request-form-status")).ToContainTextAsync("Complete");
        await Expect(t.Page.Locator("#SubmitListing")).ToBeEnabledAsync();

        await t.Page.EvaluateAsync("document.getElementById('TelegramVerificationMessage').value = 'https://example.com/not-a-telegram-message';");
        await t.Page.ClickAsync("#SubmitListing");
        var telegramFeedback = t.Page.Locator("#telegram-verification-message-error");
        await Expect(telegramFeedback).ToBeVisibleAsync();
        await Expect(telegramFeedback).ToContainTextAsync("Use a BTCPay Server Telegram message link");

        await t.Page.FillAsync("#TelegramVerificationMessage", "https://t.me/btcpayserver/1234");
        await Expect(t.Page.Locator("#TelegramVerificationMessage")).ToHaveAttributeAsync("aria-invalid", "false");
        await Expect(telegramFeedback).Not.ToBeVisibleAsync();
        await Expect(t.Page.Locator("#SubmitListing")).ToBeEnabledAsync();

        await t.Page.FillAsync("#UserReviews", "");
        await Expect(t.Page.Locator("#request-form-status")).ToContainTextAsync("Incomplete");
        await Expect(t.Page.Locator("#SubmitListing")).ToBeDisabledAsync();

        await t.Page.FillAsync("#UserReviews", "https://example.com/review");
        await Expect(t.Page.Locator("#request-form-status")).ToContainTextAsync("Complete");
        await Expect(t.Page.Locator("#SubmitListing")).ToBeEnabledAsync();
        await t.Page.ClickAsync("#SubmitListing");
        await t.AssertNoError();
        await t.Page.ClickAsync("#StoreNav-RequestListing");

        await Expect(t.Page.Locator("#collapsePluginSettings")).Not.ToBeVisibleAsync();
        await Expect(t.Page.Locator("#collapseOwnerSettings")).Not.ToBeVisibleAsync();
        await Expect(t.Page.Locator("#collapseRequestForm")).Not.ToBeVisibleAsync();
        await Expect(t.Page.Locator("#request-form-status")).ToContainTextAsync("Submitted");
        await Expect(t.Page.Locator("#request-form-status")).ToHaveClassAsync(new Regex("\\bbg-success\\b"));
        await Expect(t.Page.Locator("#request-form-status")).ToHaveClassAsync(new Regex("\\btext-black\\b"));
        await Expect(t.Page.Locator("#SubmitListing")).ToHaveCountAsync(0);
        await Expect(requestFormToggle).ToBeEnabledAsync();
        await requestFormToggle.ClickAsync();
        await Expect(t.Page.Locator("#collapseRequestForm")).ToBeVisibleAsync();
        await Expect(t.Page.Locator("#ReleaseNote")).ToHaveValueAsync("Testing release note entry");
        await Expect(t.Page.Locator("#TelegramVerificationMessage")).ToHaveValueAsync("https://t.me/btcpayserver/1234");
        await Expect(t.Page.Locator("#UserReviews")).ToHaveValueAsync("https://example.com/review");
        await Expect(t.Page.Locator("#ReleaseNote")).Not.ToBeEditableAsync();
        await Expect(t.Page.Locator("#TelegramVerificationMessage")).Not.ToBeEditableAsync();
        await Expect(t.Page.Locator("#UserReviews")).Not.ToBeEditableAsync();
        await Expect(t.Page.Locator("#AnnouncementDate")).Not.ToBeEditableAsync();
        await Expect(t.Page.Locator(".alert-warning")).ToContainTextAsync("Your listing request has been sent and is pending validation");

        var duplicateRequestResponse = await PostListingRequest(t.Page);
        Assert.True(duplicateRequestResponse.Ok);
        var requests = await conn.GetAllListingRequestsForPlugin(new PluginSlug(pluginSlug));
        var pendingRequest = Assert.Single(requests);

        // Verify the listing request was created in the database
        Assert.Equal("Testing release note entry", pendingRequest.ReleaseNote);
        Assert.Equal("https://t.me/btcpayserver/1234", pendingRequest.TelegramVerificationMessage);
        Assert.Equal("https://example.com/review", pendingRequest.UserReviews);
        Assert.Equal(PluginListingRequestStatus.Pending, pendingRequest.Status);
        Assert.Null(pendingRequest.LastReminderAt);
        await Expect(t.Page.Locator("#send-listing-reminder-form")).ToHaveCountAsync(0);

        await conn.ExecuteAsync(
            "UPDATE plugin_listing_requests SET submitted_at = CURRENT_TIMESTAMP - INTERVAL '8 days' WHERE id = @requestId",
            new { requestId = pendingRequest.Id });

        await using var firstReminderConnection = await connectionFactory.Open();
        await using var secondReminderConnection = await connectionFactory.Open();
        var reminderReservations = await Task.WhenAll(
            firstReminderConnection.TryReserveListingRequestReminder(pendingRequest.Id, TimeSpan.FromDays(7)),
            secondReminderConnection.TryReserveListingRequestReminder(pendingRequest.Id, TimeSpan.FromDays(7)));
        Assert.Single(reminderReservations, reserved => reserved);

        await conn.ExecuteAsync(
            "UPDATE plugin_listing_requests SET last_reminder_at = NULL WHERE id = @requestId",
            new { requestId = pendingRequest.Id });
        await t.Page.ReloadAsync();

        var reminderForm = t.Page.Locator("#send-listing-reminder-form");
        await Expect(reminderForm).ToBeVisibleAsync();
        var reminderAction = await reminderForm.GetAttributeAsync("action");
        Assert.NotNull(reminderAction);
        var reminderUrl = new Uri(new Uri(t.Page.Url), reminderAction).ToString();
        var reminderToken = await reminderForm
            .Locator("input[name='__RequestVerificationToken']")
            .InputValueAsync();

        var reminderStartedAt = DateTimeOffset.UtcNow;
        await reminderForm.Locator("button[type='submit']").ClickAsync();
        await Expect(t.Page.Locator(".alert-success")).ToContainTextAsync("Request listing reminders sent to admins");

        var remindedRequest = await conn.GetPendingListingRequestForPlugin(pluginSlug);
        Assert.NotNull(remindedRequest);
        Assert.NotNull(remindedRequest.LastReminderAt);
        var lastReminderAt = remindedRequest.LastReminderAt.Value;
        Assert.InRange(lastReminderAt, reminderStartedAt, DateTimeOffset.UtcNow);
        await Expect(t.Page.Locator("#send-listing-reminder-form")).ToHaveCountAsync(0);

        var reminderFormData = t.Page.Context.APIRequest.CreateFormData();
        reminderFormData.Set("__RequestVerificationToken", reminderToken);
        var blockedReminderResponse = await t.Page.Context.APIRequest.PostAsync(
            reminderUrl,
            new APIRequestContextOptions { Form = reminderFormData });
        Assert.True(blockedReminderResponse.Ok);
        Assert.Contains("Please wait 7 days before sending another reminder", await blockedReminderResponse.TextAsync());

        var blockedReminderRequest = await conn.GetPendingListingRequestForPlugin(pluginSlug);
        Assert.Equal(lastReminderAt, blockedReminderRequest?.LastReminderAt);

        await conn.ExecuteAsync(
            "UPDATE plugin_listing_requests SET last_reminder_at = CURRENT_TIMESTAMP - INTERVAL '8 days' WHERE id = @requestId",
            new { requestId = pendingRequest.Id });
        await t.Page.ReloadAsync();
        await Expect(reminderForm).ToBeVisibleAsync();

        var secondReminderStartedAt = DateTimeOffset.UtcNow;
        await reminderForm.Locator("button[type='submit']").ClickAsync();
        await Expect(t.Page.Locator(".alert-success")).ToContainTextAsync("Request listing reminders sent to admins");

        var secondRemindedRequest = await conn.GetPendingListingRequestForPlugin(pluginSlug);
        Assert.NotNull(secondRemindedRequest);
        Assert.NotNull(secondRemindedRequest.LastReminderAt);
        var secondLastReminderAt = secondRemindedRequest.LastReminderAt.Value;
        Assert.InRange(secondLastReminderAt, secondReminderStartedAt, DateTimeOffset.UtcNow);
        await Expect(reminderForm).ToHaveCountAsync(0);

        var longRejectionReason = new string('x', 1_000);
        Assert.True(await conn.RejectListingRequest(pendingRequest.Id, partialOwnerId, longRejectionReason));
        await t.Page.ReloadAsync();
        await Expect(t.Page.Locator("#collapseRequestForm")).ToBeVisibleAsync();
        await Expect(t.Page.Locator("#request-form-status")).ToContainTextAsync("Complete");
        await Expect(t.Page.Locator("#SubmitListing")).ToContainTextAsync("Re-submit");
        await Expect(t.Page.Locator("#SubmitListing")).ToBeEnabledAsync();
        await Expect(t.Page.Locator("#ReleaseNote")).ToBeEditableAsync();
        await Expect(t.Page.Locator("#TelegramVerificationMessage")).ToBeEditableAsync();
        await Expect(t.Page.Locator("#UserReviews")).ToBeEditableAsync();

        await t.Page.ClickAsync("a:text-is('View History')");
        var rejectionReasonPreview = t.Page.Locator(".listing-history-rejection-preview");
        await Expect(rejectionReasonPreview).ToHaveTextAsync(longRejectionReason);
        Assert.Equal("ellipsis", await rejectionReasonPreview.EvaluateAsync<string>(
            "element => getComputedStyle(element).textOverflow"));
        var collapsedHeight = (await rejectionReasonPreview.BoundingBoxAsync())!.Height;
        var rejectionReasonToggle = t.Page.Locator(".listing-history-rejection-toggle");
        await Expect(rejectionReasonToggle).ToHaveAttributeAsync("aria-expanded", "false");
        await rejectionReasonToggle.ClickAsync();
        await Expect(rejectionReasonPreview).ToHaveClassAsync(new Regex("\\bis-expanded\\b"));
        await Expect(rejectionReasonToggle).ToHaveAttributeAsync("aria-expanded", "true");
        await Expect(rejectionReasonToggle).ToHaveAttributeAsync("aria-label", "Collapse rejection reason");
        var expandedReasonBounds = (await rejectionReasonPreview.BoundingBoxAsync())!;
        var expandedToggleBounds = (await rejectionReasonToggle.BoundingBoxAsync())!;
        Assert.True(expandedReasonBounds.Height > collapsedHeight);
        Assert.True(expandedToggleBounds.Width >= 24 && expandedToggleBounds.Height >= 24,
            $"Expected a touch target of at least 24x24px, got {expandedToggleBounds.Width}x{expandedToggleBounds.Height}px.");
        await Expect(t.Page.Locator(".listing-history-rejection-preview")).ToHaveCountAsync(1);
        await rejectionReasonToggle.ClickAsync();
        await Expect(rejectionReasonPreview).Not.ToHaveClassAsync(new Regex("\\bis-expanded\\b"));
        await Expect(rejectionReasonToggle).ToHaveAttributeAsync("aria-expanded", "false");

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
        await t.Server.CreateAndBuildPluginAsync(userId, pluginSlug);

        // Create a listing request
        var requestId = await conn.CreateListingRequest(
            pluginSlug,
            "This is a test plugin for approval",
            "https://t.me/btcpayserver/12345",
            "https://example.com/review",
            null
        );

        // Create admin user and login
        var adminEmail = await t.CreateServerAdminAsync();
        await t.LogIn(adminEmail);

        // Navigate to listing requests page
        await t.GoToUrl("/admin/listing-requests");
        await Expect(t.Page!).ToHaveURLAsync(new Regex(".*/admin/listing-requests", RegexOptions.IgnoreCase));
        var pendingRequestsBadge = t.Page.Locator("#AdminNav-ListingRequests .badge");
        await Expect(pendingRequestsBadge).ToHaveTextAsync("1");
        await Expect(pendingRequestsBadge).ToHaveClassAsync(new Regex("\\bbg-warning\\b"));
        await Expect(pendingRequestsBadge).ToHaveClassAsync(new Regex("\\btext-black\\b"));

        // Verify the request is visible in the list - use table row to avoid ambiguity
        var row = t.Page.Locator($"tbody tr:has-text('{pluginSlug}')");
        await Expect(row).ToBeVisibleAsync();
        await Expect(row.Locator(".badge.bg-warning:text('Pending')")).ToBeVisibleAsync();

        // Click to view details - scoped to the specific row
        await row.Locator($"a[href*='/admin/listing-requests/{requestId}']").ClickAsync();
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
        var adminEmail = await t.CreateServerAdminAsync();
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

    private static async Task<IAPIResponse> PostListingRequest(IPage page)
    {
        var form = page.Locator("#request-listing-form");
        var requestVerificationToken = await form
            .Locator("input[name='__RequestVerificationToken']")
            .InputValueAsync();
        var action = await form.GetAttributeAsync("action");
        Assert.NotNull(action);

        var formData = page.Context.APIRequest.CreateFormData();
        formData.Set("__RequestVerificationToken", requestVerificationToken);
        formData.Set("ReleaseNote", "Crafted release note");
        formData.Set("TelegramVerificationMessage", "https://t.me/btcpayserver/1234");
        formData.Set("UserReviews", "https://example.com/review");

        return await page.Context.APIRequest.PostAsync(
            new Uri(new Uri(page.Url), action).ToString(),
            new APIRequestContextOptions { Form = formData });
    }

}
