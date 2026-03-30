using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit;
using Newtonsoft.Json.Linq;
using Npgsql;
using PluginBuilder.DataModels;
using PluginBuilder.Services;
using PluginBuilder.Util;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.AdminTests;

[Collection("Playwright Tests")]
public class AdminBTCPayCompatibilityUITests(ITestOutputHelper output) : PageTest
{
    private readonly XUnitLogger _log = new("AdminBTCPayCompatibilityUITests", output);

    [Fact]
    public async Task Admin_Can_Edit_BTCPay_Version_Compatibility()
    {
        await using (var release = await CreateCompatibilityContext(
            "owner@compat-ui.test",
            "admin-max-",
            "Admin compatibility UI test",
            preRelease: false))
        {
            var compatibilityModal = await OpenCompatibilityModal(release.Page, release.Manifest.Version);
            await compatibilityModal.Locator("input[name='btcpayMinVersion']").FillAsync("2.0.0.0");
            await compatibilityModal.Locator("input[name='btcpayMaxVersion']").FillAsync("3.0.0.0");
            await compatibilityModal.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();

            await Expect(release.Page).ToHaveURLAsync(new Regex($"/admin/plugins/edit/{Regex.Escape(release.PluginSlug)}\\?tab=versions$", RegexOptions.IgnoreCase));
            await Expect(release.Page.Locator(".alert-success")).ToContainTextAsync("Compatibility");

            var savedMin = await release.Connection.QuerySingleOrDefaultAsync<(int[] effective_min, bool override_enabled)?>(
                "SELECT btcpay_min_ver AS effective_min, btcpay_min_ver_override_enabled AS override_enabled FROM versions WHERE plugin_slug = @pluginSlug AND ver = @version",
                new { pluginSlug = release.PluginSlug, version = release.Manifest.Version.VersionParts });
            Assert.NotNull(savedMin);
            Assert.True(savedMin.Value.override_enabled);
            Assert.Equal(new[] { 2, 0, 0, 0 }, savedMin.Value.effective_min);

            var savedMax = await release.Connection.QuerySingleOrDefaultAsync<(int[] effective_max, bool override_enabled)?>(
                "SELECT btcpay_max_ver AS effective_max, btcpay_max_ver_override_enabled AS override_enabled FROM versions WHERE plugin_slug = @pluginSlug AND ver = @version",
                new { pluginSlug = release.PluginSlug, version = release.Manifest.Version.VersionParts });
            Assert.NotNull(savedMax);
            Assert.True(savedMax.Value.override_enabled);
            Assert.Equal(new[] { 3, 0, 0, 0 }, savedMax.Value.effective_max);

            var client = release.Tester.Server.CreateHttpClient();
            Assert.Empty(await client.GetPublishedVersions("1.9.9.9", false));
            Assert.Single(await client.GetPublishedVersions("2.0.0.0", false));
            Assert.Single(await client.GetPublishedVersions("3.0.0.0", false));
            Assert.Empty(await client.GetPublishedVersions("3.0.0.1", false));
        }

        await using (var preRelease = await CreateCompatibilityContext(
            "owner@compat-ui-prerelease.test",
            "admin-pre-",
            "Admin compatibility UI pre-release test",
            preRelease: true))
        {
            var compatibilityModal = await OpenCompatibilityModal(preRelease.Page, preRelease.Manifest.Version);
            await compatibilityModal.Locator("input[name='btcpayMaxVersion']").FillAsync("2.0.0.0");
            await compatibilityModal.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();

            await Expect(preRelease.Page).ToHaveURLAsync(new Regex($"/admin/plugins/edit/{Regex.Escape(preRelease.PluginSlug)}\\?tab=versions$", RegexOptions.IgnoreCase));
            await Expect(preRelease.Page.Locator(".alert-success")).ToContainTextAsync("Compatibility");

            var savedMax = await preRelease.Connection.QuerySingleOrDefaultAsync<(int[] effective_max, bool override_enabled)?>(
                "SELECT btcpay_max_ver AS effective_max, btcpay_max_ver_override_enabled AS override_enabled FROM versions WHERE plugin_slug = @pluginSlug AND ver = @version",
                new { pluginSlug = preRelease.PluginSlug, version = preRelease.Manifest.Version.VersionParts });
            Assert.NotNull(savedMax);
            Assert.True(savedMax.Value.override_enabled);
            Assert.Equal(new[] { 2, 0, 0, 0 }, savedMax.Value.effective_max);

            var client = preRelease.Tester.Server.CreateHttpClient();
            Assert.Single(await client.GetPublishedVersions("1.9.9.9", true));
            Assert.Single(await client.GetPublishedVersions("2.0.0.0", true));
            Assert.Empty(await client.GetPublishedVersions("2.0.0.1", true));
        }
    }

    [Fact]
    public async Task Admin_Cannot_Reset_BTCPay_Compatibility_When_Manifest_Condition_Is_Unsupported()
    {
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        await tester.StartAsync();
        await using var conn = await tester.Server.GetService<DBConnectionFactory>().Open();

        var ownerId = await tester.Server.CreateFakeUserAsync("owner@compat-ui-reset.test", confirmEmail: true, githubVerified: true);
        var pluginSlug = "admin-reset-" + Guid.NewGuid().ToString("N")[..8];
        var fullBuildId = await tester.Server.CreateAndBuildPluginAsync(ownerId, pluginSlug);
        var manifestInfoJson = await conn.QuerySingleAsync<string>(
            "SELECT manifest_info FROM builds WHERE plugin_slug = @pluginSlug AND id = @buildId",
            new { pluginSlug, buildId = fullBuildId.BuildId });
        var manifest = PluginManifest.Parse(manifestInfoJson);
        await conn.SetVersionBuild(fullBuildId, manifest.Version, manifest.BTCPayMinVersion, manifest.BTCPayMaxVersion, false);
        await conn.SetPluginSettings(pluginSlug, new PluginSettings
        {
            PluginTitle = pluginSlug,
            Description = "Admin compatibility reset test",
            GitRepository = ServerTester.RepoUrl
        }, PluginVisibilityEnum.Listed);

        await conn.ExecuteAsync(
            """
            UPDATE versions
            SET btcpay_min_ver = @minVersion,
                btcpay_min_ver_override_enabled = TRUE,
                btcpay_max_ver = @maxVersion,
                btcpay_max_ver_override_enabled = TRUE
            WHERE plugin_slug = @pluginSlug AND ver = @version
            """,
            new
            {
                pluginSlug,
                version = manifest.Version.VersionParts,
                minVersion = PluginVersion.Parse("2.0.0.0").VersionParts,
                maxVersion = PluginVersion.Parse("2.5.0.0").VersionParts
            });

        var unsupportedManifest = JObject.Parse(manifestInfoJson);
        ((JObject)unsupportedManifest["Dependencies"]![0]!)["Condition"] = ">= 1.0.0 && < 2.0.0";
        await conn.ExecuteAsync(
            "UPDATE builds SET manifest_info = @manifestInfo::jsonb WHERE plugin_slug = @pluginSlug AND id = @buildId",
            new
            {
                pluginSlug,
                buildId = fullBuildId.BuildId,
                manifestInfo = unsupportedManifest.ToString()
            });

        var adminEmail = await tester.CreateServerAdminAsync("admin-compat");
        await tester.LogIn(adminEmail);
        await tester.GoToUrl($"/admin/plugins/edit/{pluginSlug}?tab=versions");
        var page = Assert.IsAssignableFrom<IPage>(tester.Page);

        var versionRow = page.Locator("tbody tr", new PageLocatorOptions { HasTextString = manifest.Version.ToString() }).First;
        await versionRow.GetByRole(AriaRole.Button, new() { Name = $"Edit compatibility for {manifest.Version}" }).ClickAsync();
        var compatibilityModal = page.Locator("#btcpay-compatibility-modal-" + manifest.Version.ToString().Replace('.', '-'));
        await Expect(compatibilityModal).ToBeVisibleAsync();
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
            WHERE plugin_slug = @pluginSlug AND ver = @version
            """,
            new { pluginSlug, version = manifest.Version.VersionParts });

        Assert.Equal(new[] { 2, 0, 0, 0 }, savedCompatibility.effective_min);
        Assert.True(savedCompatibility.min_override_enabled);
        Assert.Equal(new[] { 2, 5, 0, 0 }, savedCompatibility.effective_max);
        Assert.True(savedCompatibility.max_override_enabled);
    }

    private async Task<CompatibilityContext> CreateCompatibilityContext(
        string ownerEmail,
        string pluginSlugPrefix,
        string description,
        bool preRelease)
    {
        var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        await tester.StartAsync();
        var conn = await tester.Server.GetService<DBConnectionFactory>().Open();

        var ownerId = await tester.Server.CreateFakeUserAsync(ownerEmail, confirmEmail: true, githubVerified: true);
        var pluginSlug = pluginSlugPrefix + Guid.NewGuid().ToString("N")[..8];
        var fullBuildId = await tester.Server.CreateAndBuildPluginAsync(ownerId, pluginSlug);
        var manifestInfoJson = await conn.QuerySingleAsync<string>(
            "SELECT manifest_info FROM builds WHERE plugin_slug = @pluginSlug AND id = @buildId",
            new { pluginSlug, buildId = fullBuildId.BuildId });
        var manifest = PluginManifest.Parse(manifestInfoJson);
        if (!preRelease)
            await conn.SetVersionBuild(fullBuildId, manifest.Version, manifest.BTCPayMinVersion, manifest.BTCPayMaxVersion, false);

        await conn.SetPluginSettings(pluginSlug, new PluginSettings
        {
            PluginTitle = pluginSlug,
            Description = description,
            GitRepository = ServerTester.RepoUrl
        }, PluginVisibilityEnum.Listed);

        var adminEmail = await tester.CreateServerAdminAsync("admin-compat");
        await tester.LogIn(adminEmail);
        await tester.GoToUrl($"/admin/plugins/edit/{pluginSlug}?tab=versions");
        var page = Assert.IsAssignableFrom<IPage>(tester.Page);
        await Expect(page).ToHaveURLAsync(new Regex($"/admin/plugins/edit/{Regex.Escape(pluginSlug)}\\?tab=versions$", RegexOptions.IgnoreCase));

        return new CompatibilityContext(tester, conn, page, pluginSlug, manifest);
    }

    private async Task<ILocator> OpenCompatibilityModal(IPage page, PluginVersion version)
    {
        var versionRow = page.Locator("tbody tr", new PageLocatorOptions { HasTextString = version.ToString() }).First;
        await versionRow.GetByRole(AriaRole.Button, new() { Name = $"Edit compatibility for {version}" }).ClickAsync();
        var compatibilityModal = page.Locator("#btcpay-compatibility-modal-" + version.ToString().Replace('.', '-'));
        await Expect(compatibilityModal).ToBeVisibleAsync();
        return compatibilityModal;
    }

    private sealed class CompatibilityContext(
        PlaywrightTester tester,
        NpgsqlConnection connection,
        IPage page,
        string pluginSlug,
        PluginManifest manifest) : IAsyncDisposable
    {
        public PlaywrightTester Tester { get; } = tester;
        public NpgsqlConnection Connection { get; } = connection;
        public IPage Page { get; } = page;
        public string PluginSlug { get; } = pluginSlug;
        public PluginManifest Manifest { get; } = manifest;

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            await Tester.DisposeAsync();
        }
    }
}
