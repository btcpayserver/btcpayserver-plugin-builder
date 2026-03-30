using System.Text.RegularExpressions;
using Microsoft.Playwright.Xunit;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.DataModels;
using PluginBuilder.Services;
using PluginBuilder.Tests.TestData;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.PluginTests;

[Collection("Playwright Tests")]
public class AccountUITests(ITestOutputHelper output) : PageTest
{
    private readonly XUnitLogger _log = new("AccountProfileTests", output);


    [Fact]
    public async Task Validate_GPG_Settings_Update_For_Release_Tests()
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

        await t.Page!.ClickAsync("#Nav-Account");
        await t.Page!.ClickAsync("#Nav-ManageAccount");
        await Expect(t.Page!).ToHaveURLAsync(new Regex(".*/account/details$", RegexOptions.IgnoreCase));
        await t.Page!.FillAsync("#Settings_GPGKey_PublicKey", "This is not a GPG key at all!");
        await t.Page!.ClickAsync("#Save");
        await Expect(t.Page!.Locator(".alert-warning")).ToContainTextAsync("GPG Key is not valid");

        await t.Page!.FillAsync("#Settings_GPGKey_PublicKey", GpgTestData.SamplePublicKey);
        await t.Page!.ClickAsync("#Save");
        await t.AssertNoError();

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
        await Expect(t.Page!.Locator("button:text-is('Sign and Release')")).Not.ToBeVisibleAsync();
        await Expect(t.Page!.Locator("button:text-is('Release')")).ToBeVisibleAsync();

        await t.Page!.ClickAsync("#StoreNav-Settings");
        var check = t.Page!.Locator("#RequireGPGSignatureForRelease");
        await Expect(check).Not.ToBeCheckedAsync();
        await check.CheckAsync();
        await t.Page!.FillAsync("#GitRepository", ServerTester.RepoUrl);
        await t.Page!.ClickAsync("button:has-text('Update')");
        await t.AssertNoError();
        await Expect(check).ToBeCheckedAsync();

        await t.Page!.ClickAsync("#Nav-Versions a >> nth=0");
        await Expect(t.Page!.Locator("button:text-is('Release')")).Not.ToBeVisibleAsync();
        await t.Page.ClickAsync("button:text-is('Sign and Release')");

        var invalidSigPath = Path.Combine(Path.GetTempPath(), $"invalid-{Guid.NewGuid():N}.asc");
        await File.WriteAllTextAsync(invalidSigPath, "-----BEGIN PGP SIGNATURE-----\nnot a real signature\n-----END PGP SIGNATURE-----\n");
        await t.Page.SetInputFilesAsync("input[name='signatureFile']", invalidSigPath);
        await t.Page.ClickAsync("button:text-is('Verify & Release')");
        await Expect(t.Page.Locator(".alert-danger, .alert-warning, .validation-summary-errors")).ToBeVisibleAsync();

        await t.Page.ClickAsync("button:text-is('Sign and Release')");
        var signedFile = GpgTestData.CopyEmbeddedSignatureToTempFile();
        await t.Page.SetInputFilesAsync("input[name='signatureFile']", signedFile);
        await t.Page.ClickAsync("button:text-is('Verify & Release')");
        await t.AssertNoError();
    }
}
