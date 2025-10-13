using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Playwright.Xunit;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.AuthTests;

[Collection("Playwright Tests")]
public class LoginPageTests(ITestOutputHelper output) : PageTest
{
    private readonly XUnitLogger _log = new("LoginUITest", output);

    [Fact]
    public async Task Login_Fails_With_InvalidCredentials()
    {
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        await tester.StartAsync();
        Assert.NotNull(tester.Page);

        await tester.LogIn("wrong-credentials@a.com");
        var errorLocator = tester.Page.Locator(".validation-summary-errors");
        await Expect(errorLocator).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Login_Succeeds_With_ValidPassword()
    {
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        await tester.StartAsync();

        await tester.GoToUrl("/register");
        var email = await tester.RegisterNewUser();
        await Expect(tester.Page!).ToHaveURLAsync(new Regex(".*/dashboard$", RegexOptions.IgnoreCase));
        await tester.Logout();
        await tester.LogIn(email);
        await Expect(tester.Page!).ToHaveURLAsync(new Regex(".*/dashboard$", RegexOptions.IgnoreCase));
    }
}
