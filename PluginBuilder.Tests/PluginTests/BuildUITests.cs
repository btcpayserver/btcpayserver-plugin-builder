using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.PluginTests;

[Collection("Playwright Tests")]
public class BuildUITests(ITestOutputHelper output) : PageTest
{
    private readonly XUnitLogger _log = new("BuildTests", output);
    private const string DirWithoutCsproj = "docs";

    [Fact]
    public async Task CreateBuild_And_ValidatesCsproj()
    {
        await using var t = new PlaywrightTester(_log) { Server = { ReuseDatabase = false } };
        await t.StartAsync();

        await t.GoToUrl("/register");
        var email = await t.RegisterNewUser();
        await t.VerifyUserAccounts(email);

        var slugFail = "cb-f-" + PlaywrightTester.GetRandomUInt256()[..8];
        await t.GoToUrl("/plugins/create");
        await t.Page!.FillAsync("#PluginSlug", slugFail);
        await t.Page!.FillAsync("#PluginTitle", slugFail);
        await t.Page!.FillAsync("#Description", "Test");
        await t.Page.ClickAsync("#Create");
        await t.AssertNoError();

        await t.GoToUrl($"/plugins/{slugFail}");
        await t.Page.ClickAsync("#CreateNewBuild");
        await t.Page.FillAsync("#GitRepository", ServerTester.RepoUrl);
        await t.Page.FillAsync("#GitRef", ServerTester.GitRef);
        await t.Page.FillAsync("#PluginDirectory", DirWithoutCsproj);
        await t.Page.FillAsync("#BuildConfig", ServerTester.BuildCfg);
        await t.Page.ClickAsync("#Create");

        var warn = t.Page.Locator(".alert-warning");
        await Expect(warn).ToBeVisibleAsync();

        // Create and build valid slug
        var slugA = "cb-a-" + PlaywrightTester.GetRandomUInt256()[..8];
        await t.GoToUrl("/plugins/create");
        await t.Page.FillAsync("#PluginSlug", slugA);
        await t.Page!.FillAsync("#PluginTitle", slugA);
        await t.Page!.FillAsync("#Description", "Test");
        await t.Page.ClickAsync("#Create");
        await t.AssertNoError();

        await t.GoToUrl($"/plugins/{slugA}");
        await t.Page.ClickAsync("#CreateNewBuild");
        await t.Page.FillAsync("#GitRepository", ServerTester.RepoUrl);
        await t.Page.FillAsync("#GitRef", ServerTester.GitRef);
        await t.Page.FillAsync("#PluginDirectory", ServerTester.PluginDir);
        await t.Page.FillAsync("#BuildConfig", ServerTester.BuildCfg);
        await t.Page.ClickAsync("#Create");

        await Expect(t.Page).ToHaveURLAsync(new Regex($@"/plugins/{Regex.Escape(slugA)}/builds/\d+$", RegexOptions.IgnoreCase));
        var m = Regex.Match(t.Page.Url, @"/builds/(\d+)$");
        Assert.True(m.Success, "Could not parse build url");
        var buildIdA = int.Parse(m.Groups[1].Value);

        // Wait for build end
        var terminal = await t.Server.WaitForBuildToFinishAsync(new FullBuildId(slugA, buildIdA));
        Assert.Equal(BuildStates.Uploaded, terminal);

        var download = t.Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Download" });
        await Expect(download).ToBeVisibleAsync();

        // Try build same repo/csproj for another slug
        var slugB = "cb-b-" + PlaywrightTester.GetRandomUInt256()[..8];
        await t.GoToUrl("/plugins/create");
        await t.Page.FillAsync("#PluginSlug", slugB);
        await t.Page!.FillAsync("#PluginTitle", slugB);
        await t.Page!.FillAsync("#Description", "Test");
        await t.Page.ClickAsync("#Create");
        await t.AssertNoError();

        await t.GoToUrl($"/plugins/{slugB}");
        await t.Page.ClickAsync("#CreateNewBuild");
        await t.Page.FillAsync("#GitRepository", ServerTester.RepoUrl);
        await t.Page.FillAsync("#GitRef", ServerTester.GitRef);
        await t.Page.FillAsync("#PluginDirectory", ServerTester.PluginDir);
        await t.Page.FillAsync("#BuildConfig", ServerTester.BuildCfg);
        await t.Page.ClickAsync("#Create");

        var warn2 = t.Page.Locator(".alert-warning");
        await Expect(warn2).ToBeVisibleAsync();
        await Expect(warn2).ToContainTextAsync(new Regex("does not belong to plugin slug", RegexOptions.IgnoreCase));
    }
}
