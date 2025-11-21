using System;
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
public class AccountUITests(ITestOutputHelper output) : PageTest
{
    private readonly XUnitLogger _log = new("AccountProfileTests", output);

    const string samplePublicKey = """
    -----BEGIN PGP PUBLIC KEY BLOCK-----
    Comment: User ID:	Satoshi <satoshinakamoto@bitcoin.com>
    Comment: Valid from:	10/29/2025 4:44 PM
    Comment: Type:	255-bit EdDSA (secret key available)
    Comment: Usage:	Signing, Encryption, Certifying User IDs
    Comment: Fingerprint:	4C6A315E0BEF6D464BD747EFF794D1D2212EFC48


    mDMEaQI2aRYJKwYBBAHaRw8BAQdAMsNY2s6u2BvbaSTT9vn6Z70q0XPAg2VIOWX8
    4c+Ss6a0JVNhdG9zaGkgPHNhdG9zaGluYWthbW90b0BiaXRjb2luLmNvbT6IkwQT
    FgoAOxYhBExqMV4L721GS9dH7/eU0dIhLvxIBQJpAjZpAhsDBQsJCAcCAiICBhUK
    CQgLAgQWAgMBAh4HAheAAAoJEPeU0dIhLvxI+18BAJI+dCs3Nd2UDTtd+RQ8krHh
    TjKEof4VWoUbU4+rlqBdAP9EgvVQ3HA11ArJ3h4zUpovQ5p4M6Cdbl3YI0tEjlCK
    Crg4BGkCNmkSCisGAQQBl1UBBQEBB0ATdbMg0bqmoiIyevarw83/g8ufIF8p5pe4
    UpXek1X2GwMBCAeIeAQYFgoAIBYhBExqMV4L721GS9dH7/eU0dIhLvxIBQJpAjZp
    AhsMAAoJEPeU0dIhLvxIGOoA/iBfNG2AwSOgXJASgFS7ANTW+6FUCylgfLUoZMaS
    xkCbAP9jqn7d655GQCYqLyBBjy33m5Ue9pVMjuUbO1AWm87NAA==
    =mUWI
    -----END PGP PUBLIC KEY BLOCK-----
    """;



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

        await t.Page!.FillAsync("#Settings_GPGKey_PublicKey", samplePublicKey);
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
        var signedFile = CopyEmbeddedSignature();
        await t.Page.SetInputFilesAsync("input[name='signatureFile']", signedFile);
        await t.Page.ClickAsync("button:text-is('Verify & Release')");
        await t.AssertNoError();
    }

    private static string CopyEmbeddedSignature()
    {
        var asm = typeof(AccountUITests).Assembly;
        using var s = asm.GetManifestResourceStream("PluginBuilder.Tests.TestData.manifest.txt.asc");
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".asc");
        using var fs = File.Create(tmp);
        s.CopyTo(fs);
        return tmp;
    }
}
