using System.Threading.Tasks;
using Microsoft.Playwright.Xunit;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.AuthTests;

public class LoginPageTests(ITestOutputHelper output) : PageTest
{
    private readonly XUnitLogger _log = new("LoginUITest", output);

    [Fact]
    public async Task Login_Fails_With_InvalidCredentials()
    {
        await using var tester = new PlaywrightTester(_log);
        await tester.StartAsync();

        await tester.GoToLogin();
        await tester.LogIn("wrong-credentials@a.com");

        await Expect(tester.Page.Locator("body")).ToContainTextAsync("Invalid login attempt");
    }

    [Fact]
    public async Task Login_Succeeds_With_ValidPassword()
    {
        await using var tester = new PlaywrightTester(_log);
        await tester.StartAsync();

        await tester.GoToUrl("/register");
        var email = await tester.RegisterNewUser();

        await Expect(tester.Page.Locator("body")).ToContainTextAsync("Builds");

        await tester.Logout();
        await tester.GoToLogin();
        await tester.LogIn(email);

        await Expect(tester.Page.Locator("body")).ToContainTextAsync("Builds");
    }
}
