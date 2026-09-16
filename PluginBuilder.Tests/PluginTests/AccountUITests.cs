using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Playwright.Xunit;
using PluginBuilder.DataModels;
using PluginBuilder.Services;
using PluginBuilder.Tests.TestData;
using PluginBuilder.Util.Extensions;
using PluginBuilder.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.PluginTests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
[Trait("Category", "ExecutorIntegration")]
public class AccountUITests(ITestOutputHelper output) : PageTest
{
    private readonly XUnitLogger _log = new("AccountProfileTests", output);

    [Fact]
    public async Task AccountDetailsPostRejectsOversizedRequestBody()
    {
        await using var t = new PlaywrightTester(_log) { Server = { ReuseDatabase = false } };
        await t.StartAsync();

        var email = $"account-limit-{Guid.NewGuid():N}@test.com";
        var userId = await t.Server.CreateFakeUserAsync(email);
        await t.LogIn(email);
        await t.GoToUrl("/account/details");

        const string submitAccountDetails =
            """
            async ([twitter, publicKey, paddingLength]) => {
                const form = document.getElementById("Save").form;
                const formData = new FormData(form);
                formData.set("Settings.Twitter", twitter);
                formData.set("Settings.GPGKey.PublicKey", publicKey);
                if (paddingLength > 0)
                    formData.set("RequestPadding", "x".repeat(paddingLength));

                try {
                    const response = await fetch(form.action, { method: "POST", body: formData });
                    return response.status;
                } catch {
                    return -1;
                }
            }
            """;

        const string savedTwitter = "https://twitter.com/saved";
        var normalStatus = await t.Page!.EvaluateAsync<int>(
            submitAccountDetails,
            new object[] { savedTwitter, GpgTestData.SamplePublicKey, 0 });
        Assert.InRange(normalStatus, 200, 299);

        await using var conn = await t.Server.GetService<DBConnectionFactory>().Open();
        var savedSettings = await conn.GetAccountDetailSettings(userId);
        Assert.Equal(savedTwitter, savedSettings?.Twitter);
        Assert.NotNull(savedSettings?.GPGKey);

        const string rejectedTwitter = "https://twitter.com/should-not-save";
        const int oversizedPaddingLength = PgpKeyViewModel.MaxArmouredPublicKeyLength * 4;
        var oversizedStatus = await t.Page.EvaluateAsync<int>(
            submitAccountDetails,
            new object[] { rejectedTwitter, GpgTestData.SamplePublicKey, oversizedPaddingLength });

        Assert.True(
            oversizedStatus is -1 or 0 or StatusCodes.Status400BadRequest or StatusCodes.Status413PayloadTooLarge,
            $"Oversized account details request returned unexpected HTTP status {oversizedStatus}.");

        var settingsAfterOversizedPost = await conn.GetAccountDetailSettings(userId);
        Assert.Equal(savedTwitter, settingsAfterOversizedPost?.Twitter);

        await t.GoToUrl("/account/details");
        await t.AssertNoError();
    }

    [Fact]
    public async Task Validate_GPG_Settings_Update_For_Release_Tests()
    {
        await using var t = new PlaywrightTester(_log);
        t.Server.ReuseDatabase = false;
        await t.StartAsync();
        await using var conn = await t.Server.GetService<DBConnectionFactory>().Open();
        await t.EnableGithubVerificationAsync(conn);

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
