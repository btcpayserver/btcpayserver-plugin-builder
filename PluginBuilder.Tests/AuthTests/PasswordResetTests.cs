using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.AuthTests;

/// <summary>
/// End-to-end password-reset flow, captured via Mailpit.
/// </summary>
[Collection("Playwright Tests")]
public class PasswordResetTests(ITestOutputHelper output) : PageTest
{
    private readonly XUnitLogger _log = new("PasswordResetTests", output);

    private const string ResetSubject = "Reset your password on BTCPay Server Plugin Builder";
    private static readonly Regex ResetLink = new(@"https?://\S+/passwordreset\S*", RegexOptions.IgnoreCase);

    [Fact]
    public async Task ForgotPassword_SendsResetEmail_AndValidTokenResetsPassword()
    {
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        await tester.StartAsync();

        await tester.ConfigureMailpitSmtp();
        var email = $"reset-{Guid.NewGuid():N}@test.com";
        await tester.CreateConfirmedUser(email);

        var message = await tester.Server.AssertHasEmail(ResetSubject, email, () => SubmitForgotPassword(tester, email));

        var link = ResetLink.Match(message.Text);
        Assert.True(link.Success, $"No passwordreset link found in body:\n{message.Text}");

        const string newPassword = "new-password-123";
        await tester.GoToEmailLink(link.Value);
        await tester.Page!.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        await tester.Page.FillAsync("#Password", newPassword);
        await tester.Page.FillAsync("#ConfirmPassword", newPassword);
        await tester.Page.ClickAsync("#ResetPassword");
        await tester.Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        await tester.LogIn(email, newPassword);
        await Expect(tester.Page).ToHaveURLAsync(new Regex(".*/dashboard$", RegexOptions.IgnoreCase));

        await tester.LogIn(email, "123456");
        await Expect(tester.Page.Locator(".validation-summary-errors")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task ForgotPassword_UnknownEmail_SendsNoEmail_ButShowsSuccess()
    {
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        await tester.StartAsync();

        await tester.ConfigureMailpitSmtp();
        var unknown = $"nobody-{Guid.NewGuid():N}@test.com";

        await SubmitForgotPassword(tester, unknown);
        await tester.Page!.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // Success is shown regardless of account existence, but no email is sent.
        await Expect(tester.Page.Locator("body"))
            .ToContainTextAsync("If an account exists for the email address you entered");

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

        await tester.ConfigureMailpitSmtp();
        var email = $"reset-bad-{Guid.NewGuid():N}@test.com";
        await tester.CreateConfirmedUser(email);

        await tester.GoToUrl($"/passwordreset?email={Uri.EscapeDataString(email)}&code=not-a-valid-token");
        await tester.Page!.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        await tester.Page.FillAsync("#Password", "new-password-123");
        await tester.Page.FillAsync("#ConfirmPassword", "new-password-123");
        await tester.Page.ClickAsync("#ResetPassword");
        await tester.Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

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

    private static async Task<bool> CheckPassword(PlaywrightTester tester, string email, string password)
    {
        using var scope = tester.Server.WebApp.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await userManager.FindByEmailAsync(email);
        Assert.NotNull(user);
        return await userManager.CheckPasswordAsync(user!, password);
    }
}
