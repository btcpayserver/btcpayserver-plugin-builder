using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.AuthTests;

/// <summary>
/// End-to-end email-verification flows (registration confirmation and email change), captured via Mailpit.
/// </summary>
[Collection("Playwright Tests")]
public class EmailVerificationTests(ITestOutputHelper output) : PageTest
{
    private readonly XUnitLogger _log = new("EmailVerificationTests", output);

    private const string VerifySubject = "Verify your account on BTCPay Server Plugin Builder";
    private static readonly Regex ConfirmLink = new(@"https?://\S+/ConfirmEmail\S*", RegexOptions.IgnoreCase);
    private static readonly Regex UpdateEmailLink = new(@"https?://\S+/UpdateEmail\S*", RegexOptions.IgnoreCase);

    [Fact]
    public async Task Registration_SendsVerifyEmail_AndValidTokenConfirms()
    {
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        await tester.StartAsync();

        // The first registered account is auto-promoted to admin and skips verification.
        await tester.ConfigureMailpitSmtp();
        await tester.CreateServerAdminAsync();

        var email = $"verify-{Guid.NewGuid():N}@test.com";
        var message = await tester.Server.AssertHasEmail(VerifySubject, email, () => RegisterViaUi(tester, email));

        var link = ConfirmLink.Match(message.Text);
        Assert.True(link.Success, $"No ConfirmEmail link found in body:\n{message.Text}");

        await tester.GoToEmailLink(link.Value);
        await tester.Page!.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        Assert.True(await IsEmailConfirmed(tester, email), "Account should be confirmed after following the verify link");
    }

    [Fact]
    public async Task ConfirmEmail_InvalidToken_DoesNotConfirm()
    {
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        await tester.StartAsync();

        await tester.ConfigureMailpitSmtp();
        await tester.CreateServerAdminAsync();

        var email = $"verify-bad-{Guid.NewGuid():N}@test.com";
        var message = await tester.Server.AssertHasEmail(VerifySubject, email, () => RegisterViaUi(tester, email));

        var link = ConfirmLink.Match(message.Text);
        Assert.True(link.Success, $"No ConfirmEmail link found in body:\n{message.Text}");

        var tampered = Regex.Replace(link.Value, @"token=[^&]+", "token=not-a-valid-token");
        await tester.GoToEmailLink(tampered);
        await tester.Page!.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        Assert.False(await IsEmailConfirmed(tester, email), "Account must not be confirmed by an invalid token");
    }

    [Fact]
    public async Task Registration_WithoutSmtpConfigured_SkipsVerification()
    {
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        await tester.StartAsync();

        await tester.CreateServerAdminAsync();

        var email = $"nosmtp-{Guid.NewGuid():N}@test.com";
        await RegisterViaUi(tester, email);

        await Expect(tester.Page!).Not.ToHaveURLAsync(new Regex("/verifyemail", RegexOptions.IgnoreCase));
        Assert.False(await IsEmailConfirmed(tester, email), "No verification email means the account stays unconfirmed");
    }

    [Fact]
    public async Task EmailChange_SendsVerifyLink_AndValidTokenUpdatesAddress()
    {
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        await tester.StartAsync();

        await tester.ConfigureMailpitSmtp();

        var oldEmail = $"old-{Guid.NewGuid():N}@test.com";
        var newEmail = $"new-{Guid.NewGuid():N}@test.com";
        var userId = await tester.CreateConfirmedUser(oldEmail);

        await tester.LogIn(await tester.CreateServerAdminAsync());
        await tester.Page!.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        var message = await tester.Server.AssertHasEmail(VerifySubject, newEmail, async () =>
        {
            await tester.GoToUrl($"/admin/userchangeemail?userId={userId}");
            await tester.Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            await tester.Page.FillAsync("#NewEmail", newEmail);
            await tester.Page.ClickAsync("#Submit");
        });

        var link = UpdateEmailLink.Match(message.Text);
        Assert.True(link.Success, $"No UpdateEmail link found in body:\n{message.Text}");

        await tester.GoToEmailLink(link.Value);
        await tester.Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        await tester.Logout();
        await tester.LogIn(newEmail);
        await Expect(tester.Page).ToHaveURLAsync(new Regex(".*/dashboard$", RegexOptions.IgnoreCase));
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

    private static async Task<bool> IsEmailConfirmed(PlaywrightTester tester, string email)
    {
        using var scope = tester.Server.WebApp.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await userManager.FindByEmailAsync(email);
        Assert.NotNull(user);
        return user!.EmailConfirmed;
    }
}
