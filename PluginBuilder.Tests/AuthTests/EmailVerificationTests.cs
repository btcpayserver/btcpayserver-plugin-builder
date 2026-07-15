using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit;
using PluginBuilder.DataModels;
using PluginBuilder.Services;
using PluginBuilder.Util;
using PluginBuilder.Util.Extensions;
using PluginBuilder.ViewModels.Admin;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.AuthTests;

/// <summary>
/// End-to-end coverage of the email-verification flows, captured via the Mailpit test harness
/// (see PluginBuilder.Tests/docker-compose.yml). Each flow mints an identity token, sends a link by
/// email, and a consumer route redeems it: registration → ConfirmEmail (initial confirmation), and an
/// email-change → VerifyEmailUpdate (swaps the account's address). Also covers the SMTP-not-configured
/// gate that skips verification entirely.
/// </summary>
[Collection("Playwright Tests")]
public class EmailVerificationTests(ITestOutputHelper output) : PageTest
{
    private readonly XUnitLogger _log = new("EmailVerificationTests", output);

    // Matches the plain-text link EmailService.SendVerifyEmail embeds in the body.
    private static readonly Regex ConfirmLink = new(@"https?://\S+/ConfirmEmail\S*", RegexOptions.IgnoreCase);
    private static readonly Regex UpdateEmailLink = new(@"https?://\S+/UpdateEmail\S*", RegexOptions.IgnoreCase);

    [Fact]
    public async Task Registration_SendsVerifyEmail_AndValidTokenConfirms()
    {
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        await tester.StartAsync();

        // An admin must already exist so the new registration is treated as a normal (non-admin) user;
        // otherwise the first account is auto-promoted to admin and skips email verification.
        await ConfigureMailpitSmtp(tester);
        await tester.CreateServerAdminAsync();

        var email = $"verify-{Guid.NewGuid():N}@test.com";
        var message = await tester.Server.AssertHasEmail(
            "Verify your account on BTCPay Server Plugin Builder",
            () => RegisterViaUi(tester, email));

        Assert.Contains(message.To, t => t.Address == email);
        var link = ConfirmLink.Match(message.Text);
        Assert.True(link.Success, $"No ConfirmEmail link found in body:\n{message.Text}");

        // Following the link consumes the token and confirms the account.
        await tester.GoToUrl(ToRelative(tester, link.Value));
        await tester.Page!.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        Assert.True(await IsEmailConfirmed(tester, email), "Account should be confirmed after following the verify link");
    }

    [Fact]
    public async Task ConfirmEmail_InvalidToken_DoesNotConfirm()
    {
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        await tester.StartAsync();

        await ConfigureMailpitSmtp(tester);
        await tester.CreateServerAdminAsync();

        var email = $"verify-bad-{Guid.NewGuid():N}@test.com";
        var message = await tester.Server.AssertHasEmail(
            "Verify your account on BTCPay Server Plugin Builder",
            () => RegisterViaUi(tester, email));

        var link = ConfirmLink.Match(message.Text);
        Assert.True(link.Success, $"No ConfirmEmail link found in body:\n{message.Text}");

        // Corrupt the token portion of the link; the identity token must fail validation.
        var tampered = Regex.Replace(link.Value, @"token=[^&]+", "token=not-a-valid-token");
        await tester.GoToUrl(ToRelative(tester, tampered));
        await tester.Page!.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        Assert.False(await IsEmailConfirmed(tester, email), "Account must not be confirmed by an invalid token");
    }

    [Fact]
    public async Task Registration_WithoutSmtpConfigured_SkipsVerification()
    {
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        await tester.StartAsync();

        // No SMTP settings saved: emailSettings?.PasswordSet is false, so the verification branch
        // is skipped and the user is signed in directly.
        await tester.CreateServerAdminAsync();

        var email = $"nosmtp-{Guid.NewGuid():N}@test.com";
        await RegisterViaUi(tester, email);

        // Signed in and landed off the verify page (no "confirm your email" gate).
        await Expect(tester.Page!).Not.ToHaveURLAsync(new Regex("/verifyemail", RegexOptions.IgnoreCase));
        Assert.False(await IsEmailConfirmed(tester, email), "No verification email means the account stays unconfirmed");
    }

    [Fact]
    public async Task EmailChange_SendsVerifyLink_AndValidTokenUpdatesAddress()
    {
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        await tester.StartAsync();

        await ConfigureMailpitSmtp(tester);

        var oldEmail = $"old-{Guid.NewGuid():N}@test.com";
        var newEmail = $"new-{Guid.NewGuid():N}@test.com";
        var userId = await CreateConfirmedUser(tester, oldEmail);

        // Drive the same producer path as AdminController.UserChangeEmail: stage the pending address,
        // mint a change-email token, and mail the /UpdateEmail link.
        var message = await tester.Server.AssertHasEmail(
            "Verify your account on BTCPay Server Plugin Builder",
            async () => await SendEmailChangeVerification(tester, userId, newEmail));

        Assert.Contains(message.To, t => t.Address == newEmail);
        var link = UpdateEmailLink.Match(message.Text);
        Assert.True(link.Success, $"No UpdateEmail link found in body:\n{message.Text}");

        // Following the link redeems the change-email token and swaps the account's address.
        await tester.GoToUrl(ToRelative(tester, link.Value));
        await tester.Page!.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        using var scope = tester.Server.WebApp.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var updated = await userManager.FindByIdAsync(userId);
        Assert.NotNull(updated);
        Assert.Equal(newEmail, updated!.Email);
    }

    // Replicates AdminController.UserChangeEmail's producer: persist PendingNewEmail, generate the
    // ChangeEmail token bound to the new address, and send the /UpdateEmail verification link.
    private static async Task SendEmailChangeVerification(PlaywrightTester tester, string userId, string newEmail)
    {
        using var scope = tester.Server.WebApp.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var emailService = scope.ServiceProvider.GetRequiredService<EmailService>();

        var user = await userManager.FindByIdAsync(userId);
        Assert.NotNull(user);

        await using var conn = await tester.Server.GetService<DBConnectionFactory>().Open();
        var settings = await conn.GetAccountDetailSettings(userId) ?? new AccountSettings();
        settings.PendingNewEmail = newEmail;
        await conn.SetAccountDetailSettings(settings, userId);

        var token = await userManager.GenerateChangeEmailTokenAsync(user!, newEmail);
        var link = $"{tester.ServerUri}UpdateEmail?uid={user!.Id}&token={Uri.EscapeDataString(token)}";
        await emailService.SendVerifyEmail(newEmail, link);
    }

    private static async Task RegisterViaUi(PlaywrightTester tester, string email)
    {
        await tester.GoToUrl("/register");
        await tester.Page!.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        await tester.Page.FillAsync("#Email", email);
        await tester.Page.FillAsync("#Password", "123456");
        await tester.Page.FillAsync("#ConfirmPassword", "123456");
        await tester.Page.ClickAsync("#RegisterButton");
        await tester.Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
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

    private static async Task<bool> IsEmailConfirmed(PlaywrightTester tester, string email)
    {
        using var scope = tester.Server.WebApp.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await userManager.FindByEmailAsync(email);
        Assert.NotNull(user);
        return user!.EmailConfirmed;
    }

    private static async Task<string> CreateConfirmedUser(PlaywrightTester tester, string email)
    {
        using var scope = tester.Server.WebApp.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = new IdentityUser { UserName = email, Email = email, EmailConfirmed = true };
        var result = await userManager.CreateAsync(user, "123456");
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Description)));
        return user.Id;
    }

    // The email carries an absolute URL bound to the test server's ephemeral port; drive it as a
    // server-relative path so Playwright navigates within the same origin.
    private static string ToRelative(PlaywrightTester tester, string absoluteUrl) =>
        new Uri(absoluteUrl).PathAndQuery;
}
