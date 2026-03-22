using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit;
using Newtonsoft.Json.Linq;
using PluginBuilder.DataModels;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.PluginTests;

[Collection("Playwright Tests")]
public class BuildUITests(ITestOutputHelper output) : PageTest
{
    private const string DirWithoutCsproj = "docs";
    private readonly XUnitLogger _log = new("BuildTests", output);

    [Fact]
    public async Task Owner_Can_Edit_BTCPay_Compatibility_From_Build_Page()
    {
        var releaseContext = await CreateBuildCompatibilityContext(
            "owner-build-min",
            "build-min-",
            "Owner compatibility build test",
            keepPreRelease: false);
        await using var releaseTester = releaseContext.Tester;
        await using var releaseConn = releaseContext.Connection;

        await releaseTester.LogIn(releaseContext.OwnerEmail);
        await releaseTester.GoToUrl($"/plugins/{releaseContext.PluginSlug}/builds/{releaseContext.FullBuildId.BuildId}");
        var releasePage = Assert.IsAssignableFrom<IPage>(releaseTester.Page);
        await Expect(releasePage).ToHaveURLAsync(new Regex($"/plugins/{Regex.Escape(releaseContext.PluginSlug)}/builds/{releaseContext.FullBuildId.BuildId}$", RegexOptions.IgnoreCase));

        var releaseCompatibilityModal = await OpenCompatibilityModal(releasePage);
        var minVersionInput = releaseCompatibilityModal.Locator("input[name='btcpayMinVersion']");
        await Expect(minVersionInput).ToBeVisibleAsync();
        await minVersionInput.FillAsync("2.0.0.0");
        await releaseCompatibilityModal.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();

        await Expect(releasePage).ToHaveURLAsync(new Regex($"/plugins/{Regex.Escape(releaseContext.PluginSlug)}/builds/{releaseContext.FullBuildId.BuildId}$", RegexOptions.IgnoreCase));
        await Expect(releasePage.Locator(".alert-success")).ToContainTextAsync("Compatibility updated successfully.");

        var savedMinVersion = await releaseConn.QuerySingleOrDefaultAsync<(int[] effective_version, bool override_enabled)?>(
            """
            SELECT btcpay_min_ver AS effective_version,
                   btcpay_min_ver_override_enabled AS override_enabled
            FROM versions
            WHERE plugin_slug = @pluginSlug AND build_id = @buildId
            """,
            new { pluginSlug = releaseContext.PluginSlug, buildId = releaseContext.FullBuildId.BuildId });
        Assert.NotNull(savedMinVersion);
        Assert.True(savedMinVersion.Value.override_enabled);
        Assert.Equal(PluginVersion.Parse("2.0.0.0").VersionParts, savedMinVersion.Value.effective_version);

        var releaseClient = releaseTester.Server.CreateHttpClient();
        Assert.Empty(await releaseClient.GetPublishedVersions("1.9.9.9", false));
        Assert.Single(await releaseClient.GetPublishedVersions("2.0.0.0", false));

        var preReleaseContext = await CreateBuildCompatibilityContext(
            "owner-build-max-pre",
            "build-max-pre-",
            "Owner compatibility pre-release build test",
            keepPreRelease: true);
        await using var preReleaseTester = preReleaseContext.Tester;
        await using var preReleaseConn = preReleaseContext.Connection;

        await preReleaseTester.LogIn(preReleaseContext.OwnerEmail);
        await preReleaseTester.GoToUrl($"/plugins/{preReleaseContext.PluginSlug}/builds/{preReleaseContext.FullBuildId.BuildId}");
        var preReleasePage = Assert.IsAssignableFrom<IPage>(preReleaseTester.Page);
        await Expect(preReleasePage).ToHaveURLAsync(new Regex($"/plugins/{Regex.Escape(preReleaseContext.PluginSlug)}/builds/{preReleaseContext.FullBuildId.BuildId}$", RegexOptions.IgnoreCase));

        var preReleaseCompatibilityModal = await OpenCompatibilityModal(preReleasePage);
        var maxVersionInput = preReleaseCompatibilityModal.Locator("input[name='btcpayMaxVersion']");
        await Expect(maxVersionInput).ToBeVisibleAsync();
        await maxVersionInput.FillAsync("2.0.0.0");
        await preReleaseCompatibilityModal.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();

        await Expect(preReleasePage).ToHaveURLAsync(new Regex($"/plugins/{Regex.Escape(preReleaseContext.PluginSlug)}/builds/{preReleaseContext.FullBuildId.BuildId}$", RegexOptions.IgnoreCase));
        await Expect(preReleasePage.Locator(".alert-success")).ToContainTextAsync("Compatibility updated successfully.");

        var savedMaxVersion = await preReleaseConn.QuerySingleOrDefaultAsync<(int[] effective_version, bool override_enabled)?>(
            """
            SELECT btcpay_max_ver AS effective_version,
                   btcpay_max_ver_override_enabled AS override_enabled
            FROM versions
            WHERE plugin_slug = @pluginSlug AND build_id = @buildId
            """,
            new { pluginSlug = preReleaseContext.PluginSlug, buildId = preReleaseContext.FullBuildId.BuildId });
        Assert.NotNull(savedMaxVersion);
        Assert.True(savedMaxVersion.Value.override_enabled);
        Assert.Equal(PluginVersion.Parse("2.0.0.0").VersionParts, savedMaxVersion.Value.effective_version);

        var preReleaseClient = preReleaseTester.Server.CreateHttpClient();
        Assert.Single(await preReleaseClient.GetPublishedVersions("1.9.9.9", true));
        Assert.Single(await preReleaseClient.GetPublishedVersions("2.0.0.0", true));
        Assert.Empty(await preReleaseClient.GetPublishedVersions("2.0.0.1", true));
    }

    [Fact]
    public async Task Owner_Cannot_Reset_BTCPay_Compatibility_When_Manifest_Condition_Is_Unsupported()
    {
        var context = await CreateBuildCompatibilityContext(
            "owner-build-reset",
            "build-reset-",
            "Owner compatibility reset test",
            keepPreRelease: false);
        await using var t = context.Tester;
        await using var conn = context.Connection;

        await conn.ExecuteAsync(
            """
            UPDATE versions
            SET btcpay_min_ver = @minVersion,
                btcpay_min_ver_override_enabled = TRUE,
                btcpay_max_ver = @maxVersion,
                btcpay_max_ver_override_enabled = TRUE
            WHERE plugin_slug = @pluginSlug AND build_id = @buildId
            """,
            new
            {
                pluginSlug = context.PluginSlug,
                buildId = context.FullBuildId.BuildId,
                minVersion = PluginVersion.Parse("2.0.0.0").VersionParts,
                maxVersion = PluginVersion.Parse("2.5.0.0").VersionParts
            });

        var unsupportedManifest = JObject.Parse(context.ManifestInfoJson);
        ((JObject)unsupportedManifest["Dependencies"]![0]!)["Condition"] = ">= 1.0.0 && < 2.0.0";
        await conn.ExecuteAsync(
            "UPDATE builds SET manifest_info = @manifestInfo::jsonb WHERE plugin_slug = @pluginSlug AND id = @buildId",
            new
            {
                pluginSlug = context.PluginSlug,
                buildId = context.FullBuildId.BuildId,
                manifestInfo = unsupportedManifest.ToString()
            });

        await t.LogIn(context.OwnerEmail);
        await t.GoToUrl($"/plugins/{context.PluginSlug}/builds/{context.FullBuildId.BuildId}");
        var page = Assert.IsAssignableFrom<IPage>(t.Page);

        var compatibilityModal = await OpenCompatibilityModal(page);
        await compatibilityModal.GetByRole(AriaRole.Button, new() { Name = "Reset to manifest" }).ClickAsync();

        await Expect(page.Locator(".alert-warning")).ToContainTextAsync("Cannot reset compatibility because the manifest BTCPayServer condition is unsupported.");
        await Expect(compatibilityModal).ToBeVisibleAsync();

        var savedCompatibility = await conn.QuerySingleAsync<(int[] effective_min, bool min_override_enabled, int[]? effective_max, bool max_override_enabled)>(
            """
            SELECT btcpay_min_ver AS effective_min,
                   btcpay_min_ver_override_enabled AS min_override_enabled,
                   btcpay_max_ver AS effective_max,
                   btcpay_max_ver_override_enabled AS max_override_enabled
            FROM versions
            WHERE plugin_slug = @pluginSlug AND build_id = @buildId
            """,
            new { pluginSlug = context.PluginSlug, buildId = context.FullBuildId.BuildId });

        Assert.Equal(new[] { 2, 0, 0, 0 }, savedCompatibility.effective_min);
        Assert.True(savedCompatibility.min_override_enabled);
        Assert.Equal(new[] { 2, 5, 0, 0 }, savedCompatibility.effective_max);
        Assert.True(savedCompatibility.max_override_enabled);
    }

    private async Task<(PlaywrightTester Tester, System.Data.Common.DbConnection Connection, string OwnerEmail, string PluginSlug, FullBuildId FullBuildId, string ManifestInfoJson)> CreateBuildCompatibilityContext(
        string ownerEmailPrefix,
        string pluginSlugPrefix,
        string description,
        bool keepPreRelease)
    {
        var t = new PlaywrightTester(_log) { Server = { ReuseDatabase = false } };
        await t.StartAsync();
        var conn = await t.Server.GetService<DBConnectionFactory>().Open();

        var ownerEmail = $"{ownerEmailPrefix}-{Guid.NewGuid():N}@test.com";
        var ownerId = await t.Server.CreateFakeUserAsync(ownerEmail, confirmEmail: true, githubVerified: true);
        var pluginSlug = pluginSlugPrefix + PlaywrightTester.GetRandomUInt256()[..8];
        var fullBuildId = await t.Server.CreateAndBuildPluginAsync(ownerId, pluginSlug);
        var manifestInfoJson = await conn.QuerySingleAsync<string>(
            "SELECT manifest_info FROM builds WHERE plugin_slug = @pluginSlug AND id = @buildId",
            new { pluginSlug, buildId = fullBuildId.BuildId });
        var manifest = PluginManifest.Parse(manifestInfoJson);
        await conn.SetVersionBuild(fullBuildId, manifest.Version, manifest.BTCPayMinVersion, manifest.BTCPayMaxVersion, keepPreRelease);
        await conn.SetPluginSettings(pluginSlug, new PluginSettings
        {
            PluginTitle = pluginSlug,
            Description = description,
            GitRepository = ServerTester.RepoUrl
        }, PluginVisibilityEnum.Listed);

        return (t, conn, ownerEmail, pluginSlug, fullBuildId, manifestInfoJson);
    }

    private async Task<ILocator> OpenCompatibilityModal(IPage page)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "Edit compatibility" }).ClickAsync();
        var compatibilityModal = page.Locator("#btcpayCompatibilityModal");
        await Expect(compatibilityModal).ToBeVisibleAsync();
        return compatibilityModal;
    }

    [Fact]
    public async Task CreateBuild_And_ValidatesCsproj()
    {
        await using var t = new PlaywrightTester(_log) { Server = { ReuseDatabase = false } };
        await t.StartAsync();

        await t.GoToUrl("/register");
        var email = await t.RegisterNewUser();

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
