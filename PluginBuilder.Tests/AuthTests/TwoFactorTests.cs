using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

    [Fact]
    public async Task EnablingNeedsAValidCodeAndSigningInThenNeedsTheSecondStep()
    {
        await using var tester = await StartServer("TwoFactorFlow");
        var email = $"2fa-{Guid.NewGuid():N}@example.com";
        await tester.CreateFakeUserAsync(email, Password);
        using var client = HttpTestHelpers.CreateBrowser(tester);
        await HttpTestHelpers.LogIn(client, email, Password);

        Assert.Contains(">Off<", await Page(client, "/account/2fa"));
        var setup = await Page(client, "/account/2fa/enable");
        var key = SharedKey(setup);
        Assert.Contains($"secret={key}&amp;issuer=", setup);

        // A wrong code does not turn 2FA on.
        using (var refused = await Post(client, "/account/2fa/enable", setup, ("Code", WrongCode(key))))
        {
            Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
            Assert.Contains("That code is not valid", await refused.Content.ReadAsStringAsync());
        }
        Assert.False(await TwoFactorEnabled(tester, email));

        // The current code turns it on and shows ten recovery codes, once and uncached.
        string[] recoveryCodes;
        using (var enabled = await Post(client, "/account/2fa/enable", setup, ("Code", Totp(key))))
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

    private async Task<ServerTester> StartServer(string name)
    {
        var tester = Create(name);
        tester.ReuseDatabase = false;
        tester.ConfigureServices = WithoutBackgroundServices;
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
