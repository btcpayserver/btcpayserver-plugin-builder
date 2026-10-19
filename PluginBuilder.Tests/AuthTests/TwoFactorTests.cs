using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PluginBuilder.HostedServices;
using PluginBuilder.Services;
using PluginBuilder.Util;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.AuthTests;

public class TwoFactorSetupTests
{
    [Fact]
    public void TheKeyIsShownInGroupsOfFourAndTheUriCarriesItWithTheIssuer()
    {
        Assert.Equal("abcd efgh ijkl mnop qr", TwoFactorSetup.FormatKey("ABCDEFGHIJKLMNOPQR"));
        Assert.Equal("otpauth://totp/BTCPay%20Plugin%20Builder:me%40example.com?secret=ABCD&issuer=BTCPay%20Plugin%20Builder&digits=6",
            TwoFactorSetup.AuthenticatorUri("me@example.com", "ABCD"));
    }

    [Theory]
    [InlineData("123 456", "123456")]
    [InlineData(" 12345-678 ", "12345678")]
    [InlineData(null, "")]
    public void TypedCodesLoseTheirSpacesAndDashes(string? typed, string expected)
    {
        Assert.Equal(expected, TwoFactorSetup.NormalizeCode(typed));
    }

    [Theory]
    [InlineData(" abcde-12345 ", "ABCDE-12345")]
    [InlineData("ABCDE 12345", "ABCDE12345")]
    public void RecoveryCodesKeepTheirDashAndCase(string typed, string expected)
    {
        Assert.Equal(expected, TwoFactorSetup.NormalizeRecoveryCode(typed));
    }
}

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class TwoFactorTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    private const string Password = "123456";

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PasswordResetAndAdministrativeLockDiscardOnlyInactiveAuthenticatorKeys(bool administrativeLock, bool twoFactorEnabled)
    {
        await using var tester = await StartServer("TwoFactorPendingKey");
        var email = $"2fa-pending-{Guid.NewGuid():N}@example.com";
        var userId = await tester.CreateFakeUserAsync(email, Password);
        using var browser = HttpTestHelpers.CreateBrowser(tester);
        await HttpTestHelpers.LogIn(browser, email, Password);
        var setup = await Page(browser, "/account/2fa/enable");
        var key = SharedKey(setup);
        string[] codes = [];
        if (twoFactorEnabled)
        {
            using var enabled = await Post(browser, "/account/2fa/enable", setup, ("Code", Totp(key)));
            Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
            codes = RecoveryCodes(await enabled.Content.ReadAsStringAsync());
            Assert.Equal(TwoFactorSetup.RecoveryCodeCount, codes.Length);
        }

        string resetToken;
        using (var scope = tester.WebApp.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var user = (await users.FindByIdAsync(userId))!;
            // Another provider may use the same name; only Identity's authenticator key is pending setup.
            Assert.True((await users.SetAuthenticationTokenAsync(user, "other-provider", "AuthenticatorKey", "keep-me")).Succeeded);
            resetToken = await users.GeneratePasswordResetTokenAsync(user);
        }
        var before = await ReadState();
        Assert.Equal(key, before.Key);
        var newPassword = administrativeLock ? Password : Password + "-new";

        if (administrativeLock)
        {
            var adminEmail = $"admin-{Guid.NewGuid():N}@example.com";
            var adminId = await tester.CreateFakeUserAsync(adminEmail, Password);
            using (var scope = tester.WebApp.Services.CreateScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
                var admin = (await users.FindByIdAsync(adminId))!;
                Assert.True((await users.AddToRoleAsync(admin, Roles.ServerAdmin)).Succeeded);
            }
            using var moderation = tester.CreateHttpClient().SetBasicAuth(adminEmail, Password);
            using var locked = await moderation.PostAsJsonAsync($"/api/v1/admin/users/{userId}/lock", new { reason = "Pending authenticator fixture" });
            Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
            Assert.Equal(twoFactorEnabled ? key : null, (await ReadState()).Key);
            using var unlocked = await moderation.PostAsJsonAsync($"/api/v1/admin/users/{userId}/unlock", new { reason = "Fixture resolved" });
            Assert.Equal(HttpStatusCode.OK, unlocked.StatusCode);
        }
        else
        {
            var resetPage = await Page(browser, "/passwordreset?email=" + Uri.EscapeDataString(email) + "&code=" + Uri.EscapeDataString(resetToken));
            using (var refused = await Post(browser, "/passwordreset", resetPage, ("Email", email),
                       ("PasswordResetToken", "invalid-reset-token"), ("Password", newPassword), ("ConfirmPassword", newPassword)))
            {
                Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
                Assert.Contains("Invalid token", await refused.Content.ReadAsStringAsync());
                Assert.Equal(before, await ReadState());
            }
            using var reset = await Post(browser, "/passwordreset", resetPage, ("Email", email),
                ("PasswordResetToken", resetToken), ("Password", newPassword), ("ConfirmPassword", newPassword));
            Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);
            Assert.Equal("/login", new Uri(browser.BaseAddress!, reset.Headers.Location!).AbsolutePath);
        }

        var after = await ReadState();
        Assert.NotEqual(before.Stamp, after.Stamp);
        Assert.Equal(twoFactorEnabled, after.Enabled);
        Assert.Equal(before.OtherTokens, after.OtherTokens);
        Assert.Equal(twoFactorEnabled ? key : null, after.Key);
        using var fresh = HttpTestHelpers.CreateBrowser(tester);
        var next = await PasswordStep(fresh, email, newPassword);
        if (twoFactorEnabled)
        {
            await AssertSignedOut(fresh);
            var codePage = await Page(fresh, next);
            using var accepted = await Post(fresh, "/login/2fa", codePage, ("TwoFactorCode", Totp(key)));
            Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
            await AssertSignedIn(fresh);
            // Both the existing authenticator and recovery codes must remain usable.
            using var recovery = HttpTestHelpers.CreateBrowser(tester);
            await PasswordStep(recovery, email, newPassword);
            var recoveryPage = await Page(recovery, "/login/recovery-code");
            using var recovered = await Post(recovery, "/login/recovery-code", recoveryPage, ("RecoveryCode", codes[0]));
            Assert.Equal(HttpStatusCode.Redirect, recovered.StatusCode);
            await AssertSignedIn(recovery);
            Assert.Equal(TwoFactorSetup.RecoveryCodeCount - 1, await RecoveryCodesLeft(tester, email));
        }
        else
        {
            await AssertSignedIn(fresh);
            var replacement = SharedKey(await Page(fresh, "/account/2fa/enable"));
            Assert.NotEqual(key, replacement);
            Assert.False(await TwoFactorEnabled(tester, email));
        }

        async Task<(string? Key, bool Enabled, string? Stamp, string OtherTokens)> ReadState()
        {
            using var scope = tester.WebApp.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var user = (await users.FindByIdAsync(userId))!;
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext<IdentityUser>>();
            var otherTokens = await db.Set<IdentityUserToken<string>>().AsNoTracking()
                .Where(t => t.UserId == userId && !(t.LoginProvider == "[AspNetUserStore]" && t.Name == "AuthenticatorKey"))
                .OrderBy(t => t.LoginProvider).ThenBy(t => t.Name).Select(t => t.LoginProvider + ":" + t.Name + ":" + t.Value).ToArrayAsync();
            return (await users.GetAuthenticatorKeyAsync(user), user.TwoFactorEnabled, user.SecurityStamp, string.Join("|", otherTokens));
        }
    }

    [Fact]
    public async Task FailedPendingKeyDeletionRollsBackThePasswordChange()
    {
        var failure = new PendingKeyDeleteFailure();
        await using var tester = await StartServer("TwoFactorPendingKeyRollback", failure);
        var email = $"2fa-pending-rollback-{Guid.NewGuid():N}@example.com";
        var userId = await tester.CreateFakeUserAsync(email, Password);
        using var browser = HttpTestHelpers.CreateBrowser(tester);
        await HttpTestHelpers.LogIn(browser, email, Password);
        var key = SharedKey(await Page(browser, "/account/2fa/enable"));
        string resetToken;
        string? stamp;
        using (var scope = tester.WebApp.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var user = (await users.FindByIdAsync(userId))!;
            stamp = user.SecurityStamp;
            resetToken = await users.GeneratePasswordResetTokenAsync(user);
        }
        var resetPage = await Page(browser, "/passwordreset?email=" + Uri.EscapeDataString(email) + "&code=" + Uri.EscapeDataString(resetToken));
        const string newPassword = Password + "-new";
        failure.Enabled = true;
        using (var failed = await Post(browser, "/passwordreset", resetPage, ("Email", email),
                   ("PasswordResetToken", resetToken), ("Password", newPassword), ("ConfirmPassword", newPassword)))
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.True(failure.WasTriggered);
        using (var scope = tester.WebApp.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var user = (await users.FindByIdAsync(userId))!;
            Assert.Equal(stamp, user.SecurityStamp);
            Assert.Equal(key, await users.GetAuthenticatorKeyAsync(user));
            Assert.True(await users.CheckPasswordAsync(user, Password));
            Assert.False(await users.CheckPasswordAsync(user, newPassword));
        }

        // A rolled-back reset must leave its original reset link usable for a retry.
        failure.Enabled = false;
        using var retried = await Post(browser, "/passwordreset", resetPage, ("Email", email),
            ("PasswordResetToken", resetToken), ("Password", newPassword), ("ConfirmPassword", newPassword));
        Assert.Equal(HttpStatusCode.Redirect, retried.StatusCode);
        using var finalScope = tester.WebApp.Services.CreateScope();
        var finalUsers = finalScope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var current = (await finalUsers.FindByIdAsync(userId))!;
        Assert.Null(await finalUsers.GetAuthenticatorKeyAsync(current));
        Assert.True(await finalUsers.CheckPasswordAsync(current, newPassword));
    }

    [Theory]
    [InlineData("GET", "enable", false)]
    [InlineData("GET", "enable", true)]
    [InlineData("POST", "enable", false)]
    [InlineData("POST", "enable", true)]
    [InlineData("POST", "disable", true)]
    [InlineData("POST", "recovery-codes", true)]
    public async Task StaleSessionsCannotAccessTwoFactorSetupOrChangeSettingsAfterAPasswordReset(string method, string action, bool prepareKey)
    {
        await using var tester = await StartServer("TwoFactorStaleSession");
        var email = $"2fa-stale-{Guid.NewGuid():N}@example.com";
        var userId = await tester.CreateFakeUserAsync(email, Password);
        var initiallyEnabled = action != "enable";
        string? key = null;
        string resetToken;
        using (var scope = tester.WebApp.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var user = (await users.FindByIdAsync(userId))!;
            if (prepareKey)
            {
                Assert.True((await users.ResetAuthenticatorKeyAsync(user)).Succeeded);
                key = await users.GetAuthenticatorKeyAsync(user);
            }
            if (initiallyEnabled)
            {
                Assert.True((await users.SetTwoFactorEnabledAsync(user, true)).Succeeded);
                Assert.NotNull(await users.GenerateNewTwoFactorRecoveryCodesAsync(user, TwoFactorSetup.RecoveryCodeCount));
            }
            resetToken = await users.GeneratePasswordResetTokenAsync(user);
        }

        using var current = HttpTestHelpers.CreateBrowser(tester);
        using var stale = HttpTestHelpers.CreateBrowser(tester);
        foreach (var browser in new[] { current, stale })
        {
            var next = await PasswordStep(browser, email, Password);
            if (initiallyEnabled)
            {
                var secondStep = await Page(browser, next);
                using var signedIn = await Post(browser, "/login/2fa", secondStep, ("TwoFactorCode", Totp(key!)));
                Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
            }
            await AssertSignedIn(browser);
        }
        var form = await Page(stale, action == "enable" && prepareKey ? "/account/2fa/enable" : "/account/2fa");
        var beforeReset = await ReadState();
        const string newPassword = Password + "-new";
        var resetPage = await Page(current, "/passwordreset?email=" + Uri.EscapeDataString(email) + "&code=" + Uri.EscapeDataString(resetToken));
        using (var reset = await Post(current, "/passwordreset", resetPage, ("Email", email),
                   ("PasswordResetToken", resetToken), ("Password", newPassword), ("ConfirmPassword", newPassword)))
        {
            Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);
            Assert.Equal("/login", new Uri(current.BaseAddress!, reset.Headers.Location!).AbsolutePath);
        }
        var beforeRequest = await ReadState();
        Assert.NotEqual(beforeReset.Stamp, beforeRequest.Stamp);
        // The cookie is still accepted within Identity's periodic validation interval.
        await AssertSignedIn(stale);

        // Use a valid current code/password so refusal depends on the stale session.
        // Without a key, an invalid enable POST must not create one while rebuilding the form.
        using var refused = method == "GET"
            ? await stale.GetAsync("/account/2fa/enable")
            : await Post(stale, "/account/2fa/" + action, form,
                action == "enable" ? ("Code", key is null ? "" : Totp(key)) : ("Password", newPassword));
        Assert.Equal(beforeRequest, await ReadState());
        Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        Assert.Equal("/login", new Uri(stale.BaseAddress!, refused.Headers.Location!).AbsolutePath);
        var body = await refused.Content.ReadAsStringAsync();
        Assert.DoesNotContain("id=\"AuthenticatorKey\"", body);
        Assert.DoesNotContain("otpauth://", body);
        Assert.Empty(RecoveryCodes(body));
        await AssertSignedOut(stale);

        async Task<(string? Stamp, string? ConcurrencyStamp, bool Enabled, string Tokens)> ReadState()
        {
            using var scope = tester.WebApp.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext<IdentityUser>>();
            var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
            var tokens = await db.Set<IdentityUserToken<string>>().AsNoTracking().Where(t => t.UserId == userId)
                .OrderBy(t => t.LoginProvider).ThenBy(t => t.Name)
                .Select(t => t.LoginProvider + ":" + t.Name + ":" + t.Value).ToArrayAsync();
            return (user.SecurityStamp, user.ConcurrencyStamp, user.TwoFactorEnabled, string.Join("|", tokens));
        }
    }

    [Fact]
    public async Task EnablingNeedsAValidCodeAndSigningInThenNeedsTheSecondStep()
    {
        await using var tester = await StartServer("TwoFactorFlow");
        var email = $"2fa-{Guid.NewGuid():N}@example.com";
        await tester.CreateFakeUserAsync(email, Password);
        using var client = HttpTestHelpers.CreateBrowser(tester);
        await HttpTestHelpers.LogIn(client, email, Password);

        Assert.Contains(">Not enabled<", await Page(client, "/account/2fa"));
        var setup = await Page(client, "/account/2fa/enable");
        var key = SharedKey(setup);
        Assert.Contains($"secret={key}&amp;issuer=", setup);
        var qrImage = Regex.Match(setup, "src=\"data:image/png;base64,([^\"]+)\"");
        Assert.True(qrImage.Success, "The setup page must display the QR code inline.");
        var authenticatorLink = Regex.Match(setup, "<a\\b(?=[^>]*\\bid=\"AuthenticatorUri\")[^>]*\\bhref=\"([^\"]+)\"");
        Assert.True(authenticatorLink.Success, "The setup page must link to the authenticator app.");
        var authenticatorUri = new Uri(WebUtility.HtmlDecode(authenticatorLink.Groups[1].Value));
        Assert.Equal("otpauth", authenticatorUri.Scheme);
        Assert.Equal("totp", authenticatorUri.Host);
        Assert.Equal($"{TwoFactorSetup.Issuer}:{email}", Uri.UnescapeDataString(authenticatorUri.AbsolutePath.TrimStart('/')));
        var authenticatorParameters = QueryHelpers.ParseQuery(authenticatorUri.Query);
        var uriKey = authenticatorParameters["secret"].ToString();
        Assert.Equal(key, uriKey);
        Assert.Equal(TwoFactorSetup.Issuer, authenticatorParameters["issuer"].ToString());
        Assert.Equal("6", authenticatorParameters["digits"].ToString());
        // The setup page carries the authenticator secret: the browser must not cache it.
        using (var again = await client.GetAsync("/account/2fa/enable"))
            Assert.True(again.Headers.CacheControl?.NoStore);

        // A wrong code does not turn 2FA on.
        using (var refused = await Post(client, "/account/2fa/enable", setup, ("Code", WrongCode(key))))
        {
            Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
            Assert.True(refused.Headers.CacheControl?.NoStore);
            var invalidSetup = await refused.Content.ReadAsStringAsync();
            Assert.Contains("That code is not valid", invalidSetup);
            Assert.Equal(key, SharedKey(invalidSetup));
            Assert.Contains(qrImage.Value, invalidSetup);
        }
        Assert.False(await TwoFactorEnabled(tester, email));

        // The current code turns it on and shows ten recovery codes, once and uncached.
        string[] recoveryCodes;
        using (var enabled = await Post(client, "/account/2fa/enable", setup, ("Code", Totp(uriKey))))
        {
            Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
            Assert.True(enabled.Headers.CacheControl?.NoStore);
            recoveryCodes = RecoveryCodes(await enabled.Content.ReadAsStringAsync());
        }
        Assert.Equal(TwoFactorSetup.RecoveryCodeCount, recoveryCodes.Length);
        Assert.True(await TwoFactorEnabled(tester, email));
        await AssertSignedIn(client); // turning it on keeps the current session

        // The password alone now leads to the second step, not to a session.
        await LogOut(client);
        var secondStep = await PasswordStep(client, email, Password);
        Assert.StartsWith("/login/2fa", secondStep);
        await AssertSignedOut(client);

        var codePage = await Page(client, secondStep);
        using (var refused = await Post(client, "/login/2fa", codePage, ("TwoFactorCode", WrongCode(key))))
        {
            Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
            Assert.Contains("Invalid authenticator code.", await refused.Content.ReadAsStringAsync());
        }
        await AssertSignedOut(client);
        using (var accepted = await Post(client, "/login/2fa", codePage, ("TwoFactorCode", Totp(key))))
            Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        await AssertSignedIn(client);

        // A recovery code signs in once.
        await LogOut(client);
        await PasswordStep(client, email, Password);
        var recoveryPage = await Page(client, "/login/recovery-code");
        using (var accepted = await Post(client, "/login/recovery-code", recoveryPage, ("RecoveryCode", recoveryCodes[0])))
            Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        await AssertSignedIn(client);

        await LogOut(client);
        await PasswordStep(client, email, Password);
        recoveryPage = await Page(client, "/login/recovery-code");
        using (var reused = await Post(client, "/login/recovery-code", recoveryPage, ("RecoveryCode", recoveryCodes[0])))
        {
            Assert.Equal(HttpStatusCode.OK, reused.StatusCode);
            Assert.Contains("Invalid recovery code.", await reused.Content.ReadAsStringAsync());
        }
        await AssertSignedOut(client);
        using (var next = await Post(client, "/login/recovery-code", recoveryPage, ("RecoveryCode", $" {recoveryCodes[1].ToLowerInvariant()} ")))
            Assert.Equal(HttpStatusCode.Redirect, next.StatusCode);
        await AssertSignedIn(client);
        Assert.Equal(TwoFactorSetup.RecoveryCodeCount - 2, await RecoveryCodesLeft(tester, email));

        // Turning it off needs the password; afterwards the password alone signs in again.
        var status = await Page(client, "/account/2fa");
        Assert.Contains($"<strong>{TwoFactorSetup.RecoveryCodeCount - 2}</strong>", status);
        using (var refused = await Post(client, "/account/2fa/disable", status, ("Password", "not-the-password")))
            Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        Assert.True(await TwoFactorEnabled(tester, email));
        using (var disabled = await Post(client, "/account/2fa/disable", status, ("Password", Password)))
            Assert.Equal(HttpStatusCode.Redirect, disabled.StatusCode);
        Assert.False(await TwoFactorEnabled(tester, email));

        // Disabling rotates the stamp; the refreshed session must immediately pass setup's validation.
        Assert.NotEmpty(SharedKey(await Page(client, "/account/2fa/enable")));

        await LogOut(client);
        var afterDisable = await PasswordStep(client, email, Password);
        Assert.DoesNotContain("/login/2fa", afterDisable);
        await AssertSignedIn(client);
    }

    [Fact]
    public async Task ThePasswordAloneDoesNotSignInATwoFactorAccount()
    {
        await using var tester = await StartServer("TwoFactorPassword");
        var email = $"2fa-pw-{Guid.NewGuid():N}@example.com";
        await tester.CreateFakeUserAsync(email, Password);
        await TurnOnTwoFactor(tester, email);
        using var client = HttpTestHelpers.CreateBrowser(tester);

        Assert.StartsWith("/login/2fa", await PasswordStep(client, email, Password));
        await AssertSignedOut(client);

        // The second step is not reachable without the password step.
        using var fresh = HttpTestHelpers.CreateBrowser(tester);
        using var direct = await fresh.GetAsync("/login/2fa");
        Assert.Equal(HttpStatusCode.Redirect, direct.StatusCode);
        Assert.StartsWith("/login", direct.Headers.Location!.OriginalString);
        Assert.DoesNotContain("/login/2fa", direct.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task RecoveryCodesCanBeReplacedWithThePassword()
    {
        await using var tester = await StartServer("TwoFactorRegenerate");
        var email = $"2fa-regen-{Guid.NewGuid():N}@example.com";
        await tester.CreateFakeUserAsync(email, Password);
        using var client = HttpTestHelpers.CreateBrowser(tester);
        await HttpTestHelpers.LogIn(client, email, Password);
        var setup = await Page(client, "/account/2fa/enable");
        var key = SharedKey(setup);
        string[] first;
        using (var enabled = await Post(client, "/account/2fa/enable", setup, ("Code", Totp(key))))
            first = RecoveryCodes(await enabled.Content.ReadAsStringAsync());

        var status = await Page(client, "/account/2fa");
        string[] second;
        using (var regenerated = await Post(client, "/account/2fa/recovery-codes", status, ("Password", Password)))
        {
            Assert.Equal(HttpStatusCode.OK, regenerated.StatusCode);
            Assert.True(regenerated.Headers.CacheControl?.NoStore);
            second = RecoveryCodes(await regenerated.Content.ReadAsStringAsync());
        }
        Assert.Equal(TwoFactorSetup.RecoveryCodeCount, second.Length);
        Assert.Empty(first.Intersect(second));

        // The old codes are gone.
        await LogOut(client);
        await PasswordStep(client, email, Password);
        var recoveryPage = await Page(client, "/login/recovery-code");
        using (var old = await Post(client, "/login/recovery-code", recoveryPage, ("RecoveryCode", first[0])))
            Assert.Equal(HttpStatusCode.OK, old.StatusCode);
        using (var fresh = await Post(client, "/login/recovery-code", recoveryPage, ("RecoveryCode", second[0])))
            Assert.Equal(HttpStatusCode.Redirect, fresh.StatusCode);
    }

    [Theory]
    [InlineData("not-the-password")]
    [InlineData("")]
    public async Task RecoveryCodeReplacementRequiresTheCurrentPassword(string password)
    {
        await using var tester = await StartServer("TwoFactorRegeneratePassword");
        var email = $"2fa-regen-password-{Guid.NewGuid():N}@example.com";
        await tester.CreateFakeUserAsync(email, Password);
        using var client = HttpTestHelpers.CreateBrowser(tester);
        await HttpTestHelpers.LogIn(client, email, Password);
        var setup = await Page(client, "/account/2fa/enable");
        string[] codes;
        using (var enabled = await Post(client, "/account/2fa/enable", setup, ("Code", Totp(SharedKey(setup)))))
        {
            Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
            codes = RecoveryCodes(await enabled.Content.ReadAsStringAsync());
        }
        Assert.Equal(TwoFactorSetup.RecoveryCodeCount, codes.Length);

        var status = await Page(client, "/account/2fa");
        using (var refused = await Post(client, "/account/2fa/recovery-codes", status, ("Password", password)))
        {
            Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
            Assert.Equal("/account/2fa", new Uri(client.BaseAddress!, refused.Headers.Location!).AbsolutePath);
        }
        var error = await Page(client, "/account/2fa");
        Assert.Contains("The password is not correct.", error);
        Assert.Empty(RecoveryCodes(error));
        Assert.Equal(TwoFactorSetup.RecoveryCodeCount, await RecoveryCodesLeft(tester, email));

        // Refusing the replacement must leave the saved codes usable for sign-in.
        await LogOut(client);
        Assert.StartsWith("/login/2fa", await PasswordStep(client, email, Password));
        var recoveryPage = await Page(client, "/login/recovery-code");
        using (var accepted = await Post(client, "/login/recovery-code", recoveryPage, ("RecoveryCode", codes[0])))
            Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        await AssertSignedIn(client);
    }

    [Fact]
    public async Task BasicAuthOnTheApiStaysRefusedForATwoFactorAccount()
    {
        await using var tester = await StartServer("TwoFactorBasicAuth");
        var email = $"2fa-api-{Guid.NewGuid():N}@example.com";
        var userId = await tester.CreateFakeUserAsync(email, Password);
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        var slug = new PluginSlug($"twofactor-{Guid.NewGuid():N}"[..20]);
        await conn.NewPlugin(slug, userId);
        using var api = tester.CreateHttpClient().SetBasicAuth(email, Password);

        using (var before = await api.GetAsync($"/api/v1/plugins/{slug}/builds"))
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        // Personal API tokens (the next PR) are how an account with 2FA reaches the API.
        await TurnOnTwoFactor(tester, email);
        using (var after = await api.GetAsync($"/api/v1/plugins/{slug}/builds"))
            Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecoveryCodesRespectAccountEligibilityAfterThePasswordStep(bool lockedOut)
    {
        await using var tester = await StartServer("TwoFactorEligibility");
        var email = $"2fa-eligibility-{Guid.NewGuid():N}@example.com";
        await tester.CreateFakeUserAsync(email, Password);
        await TurnOnTwoFactor(tester, email);
        string[] codes;
        using (var scope = tester.WebApp.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var user = (await users.FindByEmailAsync(email))!;
            codes = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, TwoFactorSetup.RecoveryCodeCount))!.ToArray();
        }
        using var client = HttpTestHelpers.CreateBrowser(tester);
        await PasswordStep(client, email, Password);
        var recoveryPage = await Page(client, "/login/recovery-code");

        using (var scope = tester.WebApp.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var user = (await users.FindByEmailAsync(email))!;
            if (lockedOut)
                Assert.True((await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddMinutes(5))).Succeeded);
            else
            {
                user.EmailConfirmed = false;
                Assert.True((await users.UpdateAsync(user)).Succeeded);
                tester.GetService<IOptions<IdentityOptions>>().Value.SignIn.RequireConfirmedEmail = true;
            }
        }

        using (var refused = await Post(client, "/login/recovery-code", recoveryPage, ("RecoveryCode", codes[0])))
        {
            Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
            Assert.Contains(lockedOut ? "Too many failed attempts" : "Invalid login attempt", await refused.Content.ReadAsStringAsync());
        }
        await AssertSignedOut(client);
        Assert.Equal(TwoFactorSetup.RecoveryCodeCount, await RecoveryCodesLeft(tester, email));

        using (var scope = tester.WebApp.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var user = (await users.FindByEmailAsync(email))!;
            if (lockedOut)
                Assert.True((await users.SetLockoutEndDateAsync(user, null)).Succeeded);
            else
            {
                user.EmailConfirmed = true;
                Assert.True((await users.UpdateAsync(user)).Succeeded);
            }
        }
        using (var accepted = await Post(client, "/login/recovery-code", recoveryPage, ("RecoveryCode", codes[0])))
            Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        await AssertSignedIn(client);
        Assert.Equal(TwoFactorSetup.RecoveryCodeCount - 1, await RecoveryCodesLeft(tester, email));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task FailedEnablingDoesNotLeaveTwoFactorPartiallyEnabled(int failedUpdate)
    {
        var failure = new FailedIdentityUpdate();
        await using var tester = await StartServer("TwoFactorEnableFailure", failure);
        var email = $"2fa-enable-failure-{Guid.NewGuid():N}@example.com";
        await tester.CreateFakeUserAsync(email, Password);
        using var client = HttpTestHelpers.CreateBrowser(tester);
        await HttpTestHelpers.LogIn(client, email, Password);
        var setup = await Page(client, "/account/2fa/enable");
        var key = SharedKey(setup);

        failure.FailOnUpdate = failedUpdate;
        using (var refused = await Post(client, "/account/2fa/enable", setup, ("Code", Totp(key))))
            Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        Assert.True(failure.WasTriggered);
        Assert.False(await TwoFactorEnabled(tester, email));
        Assert.Equal(0, await RecoveryCodesLeft(tester, email));
        Assert.Contains("could not be saved", await Page(client, "/account/2fa"));
        await AssertSignedIn(client);

        using var retried = await Post(client, "/account/2fa/enable", setup, ("Code", Totp(key)));
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal(TwoFactorSetup.RecoveryCodeCount, RecoveryCodes(await retried.Content.ReadAsStringAsync()).Length);
        Assert.True(await TwoFactorEnabled(tester, email));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task FailedDisablingKeepsTheAuthenticatorAndRecoveryCodes(int failedUpdate)
    {
        var failure = new FailedIdentityUpdate();
        await using var tester = await StartServer("TwoFactorDisableFailure", failure);
        var email = $"2fa-disable-failure-{Guid.NewGuid():N}@example.com";
        await tester.CreateFakeUserAsync(email, Password);
        using var client = HttpTestHelpers.CreateBrowser(tester);
        await HttpTestHelpers.LogIn(client, email, Password);
        var setup = await Page(client, "/account/2fa/enable");
        var key = SharedKey(setup);
        using (var enabled = await Post(client, "/account/2fa/enable", setup, ("Code", Totp(key))))
            Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        var status = await Page(client, "/account/2fa");

        failure.FailOnUpdate = failedUpdate;
        using (var refused = await Post(client, "/account/2fa/disable", status, ("Password", Password)))
            Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        Assert.True(failure.WasTriggered);
        Assert.True(await TwoFactorEnabled(tester, email));
        Assert.Equal(TwoFactorSetup.RecoveryCodeCount, await RecoveryCodesLeft(tester, email));
        var error = await Page(client, "/account/2fa");
        Assert.Contains("could not be saved", error);
        Assert.DoesNotContain("Two-factor authentication is off.", error);

        await LogOut(client);
        await PasswordStep(client, email, Password);
        var codePage = await Page(client, "/login/2fa");
        using (var accepted = await Post(client, "/login/2fa", codePage, ("TwoFactorCode", Totp(key))))
            Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        await AssertSignedIn(client);
    }

    [Fact]
    public async Task FailedRecoveryCodeReplacementPreservesThePreviousCodes()
    {
        var failure = new FailedIdentityUpdate();
        await using var tester = await StartServer("TwoFactorRecoveryFailure", failure);
        var email = $"2fa-recovery-failure-{Guid.NewGuid():N}@example.com";
        await tester.CreateFakeUserAsync(email, Password);
        using var client = HttpTestHelpers.CreateBrowser(tester);
        await HttpTestHelpers.LogIn(client, email, Password);
        var setup = await Page(client, "/account/2fa/enable");
        string[] codes;
        using (var enabled = await Post(client, "/account/2fa/enable", setup, ("Code", Totp(SharedKey(setup)))))
            codes = RecoveryCodes(await enabled.Content.ReadAsStringAsync());
        var status = await Page(client, "/account/2fa");

        failure.FailOnUpdate = 1;
        using (var refused = await Post(client, "/account/2fa/recovery-codes", status, ("Password", Password)))
            Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        Assert.True(failure.WasTriggered);
        Assert.Contains("could not be saved", await Page(client, "/account/2fa"));
        Assert.Equal(TwoFactorSetup.RecoveryCodeCount, await RecoveryCodesLeft(tester, email));

        await LogOut(client);
        await PasswordStep(client, email, Password);
        var recoveryPage = await Page(client, "/login/recovery-code");
        using (var accepted = await Post(client, "/login/recovery-code", recoveryPage, ("RecoveryCode", codes[0])))
            Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        await AssertSignedIn(client);
    }

    [Fact]
    public async Task FailedKeyCreationDoesNotExposeAnUnsavedAuthenticatorKey()
    {
        var failure = new FailedIdentityUpdate();
        await using var tester = await StartServer("TwoFactorKeyFailure", failure);
        var email = $"2fa-key-failure-{Guid.NewGuid():N}@example.com";
        await tester.CreateFakeUserAsync(email, Password);
        using var client = HttpTestHelpers.CreateBrowser(tester);
        await HttpTestHelpers.LogIn(client, email, Password);

        failure.FailOnUpdate = 1;
        using (var refused = await client.GetAsync("/account/2fa/enable"))
            Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        Assert.True(failure.WasTriggered);
        Assert.Contains("could not be saved", await Page(client, "/account/2fa"));
        using (var scope = tester.WebApp.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            Assert.Null(await users.GetAuthenticatorKeyAsync((await users.FindByEmailAsync(email))!));
        }
        Assert.NotEmpty(SharedKey(await Page(client, "/account/2fa/enable")));
    }

    private sealed class FailedIdentityUpdate : SaveChangesInterceptor
    {
        public int FailOnUpdate { get; set; }
        public bool WasTriggered { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var user = eventData.Context!.ChangeTracker.Entries<IdentityUser>()
                .SingleOrDefault(entry => entry.State == EntityState.Modified);
            if (user is not null && FailOnUpdate > 0 && --FailOnUpdate == 0)
            {
                WasTriggered = true;
                // Force EF's real concurrency check to fail, including its transaction rollback.
                user.Property(u => u.ConcurrencyStamp).OriginalValue = $"stale-{Guid.NewGuid():N}";
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class PendingKeyDeleteFailure : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public bool WasTriggered { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && command.CommandText.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("\"AspNetUserTokens\""))
            {
                WasTriggered = true;
                throw new InvalidOperationException("Fixture failure while deleting the pending authenticator key.");
            }
            return ValueTask.FromResult(result);
        }
    }

    private async Task<ServerTester> StartServer(string name, IInterceptor? interceptor = null)
    {
        var tester = Create(name);
        tester.ReuseDatabase = false;
        tester.ConfigureServices = services =>
        {
            WithoutBackgroundServices(services);
            if (interceptor is not null)
            {
                services.AddDbContext<IdentityDbContext<IdentityUser>>(options => options.AddInterceptors(interceptor));
            }
        };
        await tester.Start();
        return tester;
    }

    // Accounts need only the database: drop the other hosted services (storage, builds), as the Turnstile tests do.
    private static void WithoutBackgroundServices(IServiceCollection services)
    {
        foreach (var service in services.Where(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType != typeof(DatabaseStartupHostedService)).ToArray())
            services.Remove(service);
    }

    private static async Task TurnOnTwoFactor(ServerTester tester, string email)
    {
        using var scope = tester.WebApp.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = (await users.FindByEmailAsync(email))!;
        await users.ResetAuthenticatorKeyAsync(user);
        Assert.True((await users.SetTwoFactorEnabledAsync(user, true)).Succeeded);
    }

    private static async Task<bool> TwoFactorEnabled(ServerTester tester, string email)
    {
        using var scope = tester.WebApp.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        return await users.GetTwoFactorEnabledAsync((await users.FindByEmailAsync(email))!);
    }

    private static async Task<int> RecoveryCodesLeft(ServerTester tester, string email)
    {
        using var scope = tester.WebApp.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        return await users.CountRecoveryCodesAsync((await users.FindByEmailAsync(email))!);
    }

    private static async Task<string> Page(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static Task<HttpResponseMessage> Post(HttpClient client, string url, string pageWithForm, params (string Name, string Value)[] fields)
    {
        var form = fields.ToDictionary(f => f.Name, f => f.Value);
        form["__RequestVerificationToken"] = HttpTestHelpers.AntiforgeryToken(pageWithForm);
        return client.PostAsync(url, new FormUrlEncodedContent(form));
    }

    // Returns where the password step sends the browser.
    private static async Task<string> PasswordStep(HttpClient client, string email, string password)
    {
        var login = await Page(client, "/login");
        using var response = await Post(client, "/login", login, ("Email", email), ("Password", password));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response.Headers.Location!.OriginalString;
    }

    private static async Task LogOut(HttpClient client)
    {
        var page = await Page(client, "/account/details");
        using var response = await Post(client, "/logout", page);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await AssertSignedOut(client);
    }

    private static async Task AssertSignedIn(HttpClient client)
    {
        using var response = await client.GetAsync("/account/details");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task AssertSignedOut(HttpClient client)
    {
        using var response = await client.GetAsync("/account/details");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/login", response.Headers.Location!.OriginalString);
    }

    private static string SharedKey(string setupPage)
    {
        var match = Regex.Match(setupPage, "<code id=\"AuthenticatorKey\"[^>]*>([^<]+)</code>");
        Assert.True(match.Success, "The setup page must show the shared key.");
        return match.Groups[1].Value.Replace(" ", string.Empty).ToUpperInvariant();
    }

    private static string[] RecoveryCodes(string page) =>
        Regex.Matches(page, "<li class=\"recovery-code\">([^<]+)</li>").Select(m => m.Groups[1].Value.Trim()).ToArray();

    // RFC 6238 TOTP (HMAC-SHA1, 30-second steps, 6 digits), as an authenticator app computes it.
    private static string Totp(string base32Key, long stepOffset = 0)
    {
        var counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 + stepOffset;
        var message = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(message);
        var hash = HMACSHA1.HashData(Base32Decode(base32Key), message);
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    // A six-digit code that is not valid in any step Identity accepts around now.
    private static string WrongCode(string base32Key)
    {
        var valid = Enumerable.Range(-3, 7).Select(i => Totp(base32Key, i)).ToHashSet();
        return Enumerable.Range(0, 100).Select(i => i.ToString("D6")).First(c => !valid.Contains(c));
    }

    private static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in input.TrimEnd('='))
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)((buffer >> bits) & 0xff));
            }
        }

        return output.ToArray();
    }
}
