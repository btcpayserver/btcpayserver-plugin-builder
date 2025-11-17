using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Playwright.Xunit;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.DataModels;
using PluginBuilder.Services;
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

        var pluginSettings = await conn.GetSettings(pluginSlug);
        await conn.SetPluginSettings(pluginSlug, pluginSettings, "listed");
        await t.Page!.ClickAsync("#StoreNav-Dashboard");
        var buttonCount = await t.Page.Locator("a.btn.btn-primary:has-text('Request Listing')").CountAsync();
        Assert.Equal(0, buttonCount);
    }
}

