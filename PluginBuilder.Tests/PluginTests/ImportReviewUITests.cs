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
public class ImportReviewUITests(ITestOutputHelper output) : PageTest
{
    private readonly XUnitLogger _log = new("ImportReviewTests", output);

    [Fact]
    public async Task Import_Review_For_Exisitng_User_Tests()
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
        string pluginReview = "An awesome plugin";
        await Expect(t.Page!.Locator("button:text-is('Release')")).ToBeVisibleAsync();
        await t.Page.ClickAsync("button:text-is('Release')");
        await t.Page!.ClickAsync("#AdminNav-Plugins");
        await t.Page.ClickAsync("table tbody tr:first-child a:text-is('Edit')");
        await t.Page.ClickAsync("a.btn.btn-primary:has-text('Import Reviews')");
        await t.Page.FillAsync("#SourceUrl", "https://primal.net/e/nevent1qqswdrazgv99sp5tdrqre9ez3h6xf62u82mctp042tv8s0shaswx5kqx2trp3");
        await t.Page.FillAsync("#Body", pluginReview);
        await t.Page.Locator("#Rating").FillAsync("4");
        await t.Page.ClickAsync("button[type='submit'][form='import-review-form']");
        await t.AssertNoError();
        await t.GoToUrl($"/public/plugins/{pluginSlug}");
        await Expect(t.Page.Locator(".test-review-card")).ToBeVisibleAsync();
        var ratingLocator = t.Page.Locator(".test-review-rating[data-rating='4']");
        await Expect(ratingLocator).ToBeVisibleAsync();
        await Expect(t.Page.Locator(".test-review-card")).ToContainTextAsync(pluginReview);
        var filledStars = t.Page.Locator(".test-review-rating[data-rating='4'] .text-warning");
        await Expect(filledStars).ToHaveCountAsync(4);
        var emptyStars = t.Page.Locator(".test-review-rating[data-rating='4'] .text-secondary");
        await Expect(emptyStars).ToHaveCountAsync(1);
        await Expect(t.Page.Locator("a[href*='RatingFilter=4']")).ToContainTextAsync("1");
    }

    [Fact]
    public async Task Import_Review_For_External_Tests()
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
        string pluginReview = "An awesome plugin";
        await Expect(t.Page!.Locator("button:text-is('Release')")).ToBeVisibleAsync();
        await t.Page.ClickAsync("button:text-is('Release')");
        await t.Page!.ClickAsync("#AdminNav-Plugins");
        await t.Page.ClickAsync("table tbody tr:first-child a:text-is('Edit')");
        await t.Page.ClickAsync("a.btn.btn-primary:has-text('Import Reviews')");
        await t.Page.FillAsync("#SourceUrl", "https://primal.net/e/nevent1qqswdrazgv99sp5tdrqre9ez3h6xf62u82mctp042tv8s0shaswx5kqx2trp3");
        await t.Page.FillAsync("#Body", pluginReview);
        await t.Page.Locator("#LinkExistingUser").UncheckAsync();
        await t.Page.Locator("#platformSelect").SelectOptionAsync("2");
        await t.Page.FillAsync("#ReviewerAvatarUrl", "https://avatars.githubusercontent.com/NicolasDorier");
        await t.Page.FillAsync("#ReviewerName", "NicolasDorier");
        await t.Page.ClickAsync("button[type='submit'][form='import-review-form']");
        await t.AssertNoError();
        await t.GoToUrl($"/public/plugins/{pluginSlug}");
        await Expect(t.Page.Locator(".test-review-card")).ToBeVisibleAsync();
        var ratingLocator = t.Page.Locator(".test-review-rating[data-rating='5']");
        await Expect(ratingLocator).ToBeVisibleAsync();
        await Expect(t.Page.Locator(".test-review-card")).ToContainTextAsync(pluginReview);
        var filledStars = t.Page.Locator(".test-review-rating[data-rating='5'] .text-warning");
        await Expect(filledStars).ToHaveCountAsync(5);
        var emptyStars = t.Page.Locator(".test-review-rating[data-rating='5'] .text-secondary");
        await Expect(emptyStars).ToHaveCountAsync(0);
        await Expect(t.Page.Locator("a[href*='RatingFilter=5']")).ToContainTextAsync("1");
    }
}
