using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit;
using PluginBuilder.Services;
using PluginBuilder.Util;
using PluginBuilder.ViewModels.Admin;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.AuthTests;

/// <summary>
/// End-to-end coverage of the password-reset flow, captured via the Mailpit test harness
/// (see PluginBuilder.Tests/docker-compose.yml). ForgotPassword mints a reset token and emails a link;
/// PasswordReset consumes it. Also asserts the anti-enumeration property: an unknown email produces a
/// success page but no email.
/// </summary>
[Collection("Playwright Tests")]
public class PasswordResetTests(ITestOutputHelper output) : PageTest
{
    private readonly XUnitLogger _log = new("PasswordResetTests", output);

    // Matches the plain-text link EmailService.ResetPasswordEmail embeds in the body.
    private static readonly Regex ResetLink = new(@"https?://\S+/passwordreset\S*", RegexOptions.IgnoreCase);

    [Fact]
    public async Task ForgotPassword_SendsResetEmail_AndValidTokenResetsPassword()
    {
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        await tester.StartAsync();

        await ConfigureMailpitSmtp(tester);
        var email = $"reset-{Guid.NewGuid():N}@test.com";
        await CreateConfirmedUser(tester, email, "123456");

        var message = await tester.Server.AssertHasEmail(
            "Reset your password on BTCPay Server Plugin Builder",
            () => SubmitForgotPassword(tester, email));

        Assert.Contains(message.To, t => t.Address == email);
        var link = ResetLink.Match(message.Text);
        Assert.True(link.Success, $"No passwordreset link found in body:\n{message.Text}");

        // Follow the emailed link (token arrives in the query and pre-fills the hidden field), set a
        // new password, and submit.
        const string newPassword = "new-password-123";
        await tester.GoToUrl(ToRelative(tester, link.Value));
        await tester.Page!.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        await tester.Page.FillAsync("#Password", newPassword);
        await tester.Page.FillAsync("#ConfirmPassword", newPassword);
        await tester.Page.ClickAsync("#ResetPassword");
        await tester.Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // The new password must now authenticate; the old one must not.
        Assert.True(await CheckPassword(tester, email, newPassword), "New password should be valid after reset");
        Assert.False(await CheckPassword(tester, email, "123456"), "Old password should no longer be valid");
    }

    [Fact]
    public async Task ForgotPassword_UnknownEmail_SendsNoEmail_ButShowsSuccess()
    {
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        await tester.StartAsync();

        await ConfigureMailpitSmtp(tester);
        var unknown = $"nobody-{Guid.NewGuid():N}@test.com";

        await SubmitForgotPassword(tester, unknown);
        await tester.Page!.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // Anti-enumeration: the form reports success regardless of whether the account exists.
        await Expect(tester.Page.Locator("body")).ToContainTextAsync(new Regex("check your email|email", RegexOptions.IgnoreCase));

        // But no reset email should have been sent for a non-existent account.
        using var client = tester.Server.GetMailPitClient();
        var search = await client.Search($"to:\"{unknown}\"");
        Assert.Empty(search.Messages);
    }

    [Fact]
    public async Task PasswordReset_InvalidToken_ReturnsError_PasswordUnchanged()
    {
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        await tester.StartAsync();

        await ConfigureMailpitSmtp(tester);
        var email = $"reset-bad-{Guid.NewGuid():N}@test.com";
        await CreateConfirmedUser(tester, email, "123456");

        // Land on the reset page with a bogus token (as if from a tampered/expired link).
        await tester.GoToUrl($"/passwordreset?email={Uri.EscapeDataString(email)}&code=not-a-valid-token");
        await tester.Page!.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        await tester.Page.FillAsync("#Password", "new-password-123");
        await tester.Page.FillAsync("#ConfirmPassword", "new-password-123");
        await tester.Page.ClickAsync("#ResetPassword");
        await tester.Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // ResetPasswordAsync fails, so the page redisplays with the "Invalid token." validation error
        // and the password stays unchanged.
        await Expect(tester.Page.Locator(".validation-summary-errors")).ToContainTextAsync("Invalid token");
        Assert.True(await CheckPassword(tester, email, "123456"), "Password must be unchanged after an invalid-token reset");
    }

    private static async Task SubmitForgotPassword(PlaywrightTester tester, string email)
    {
        await tester.GoToUrl("/forgotpassword");
        await tester.Page!.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        await tester.Page.FillAsync("#Email", email);
        await tester.Page.ClickAsync("#ResetPassword");
        await tester.Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
    }

    private static async Task CreateConfirmedUser(PlaywrightTester tester, string email, string password)
    {
        using var scope = tester.Server.WebApp.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = new IdentityUser { UserName = email, Email = email, EmailConfirmed = true };
        var result = await userManager.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Description)));
    }

    private static async Task<bool> CheckPassword(PlaywrightTester tester, string email, string password)
    {
        using var scope = tester.Server.WebApp.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await userManager.FindByEmailAsync(email);
        Assert.NotNull(user);
        return await userManager.CheckPasswordAsync(user!, password);
    }

    private static Task ConfigureMailpitSmtp(PlaywrightTester tester) =>
        tester.Server.GetService<EmailService>().SaveEmailSettingsToDatabase(new EmailSettingsViewModel
        {
            Server = MailpitDevSettings.Host,
            Port = MailpitDevSettings.SmtpPort,
            Username = "plugin-builder@example.com",
            From = "plugin-builder@example.com",
            Password = "password",
            DisableCertificateCheck = true
        });

    // The email carries an absolute URL bound to the test server's ephemeral port; drive it as a
    // server-relative path so Playwright navigates within the same origin.
    private static string ToRelative(PlaywrightTester tester, string absoluteUrl) =>
        new Uri(absoluteUrl).PathAndQuery;
}
