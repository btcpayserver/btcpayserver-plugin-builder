using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright.Xunit;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.DataModels;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.PluginTests;

[Collection("Playwright Tests")]
public class LogoUploadTests(ITestOutputHelper output) : PageTest
{
    private readonly XUnitLogger _log = new("LogoUploadTests", output);

    [Fact]
    public async Task Dashboard_Logo_Upload_Uses_Unique_Filename()
    {
        await using var t = new PlaywrightTester(_log);
        t.Server.ReuseDatabase = false;
        await t.StartAsync();

        await using var conn = await t.Server.GetService<DBConnectionFactory>().Open();
        await conn.SettingsSetAsync(SettingsKeys.VerifiedGithub, "true");
        var verfCache = t.Server.GetService<AdminSettingsCache>();
        await verfCache.RefreshAllAdminSettings(conn);

        // Register user
        await t.GoToUrl("/register");
        var user = await t.RegisterNewUser();
        await Expect(t.Page!).ToHaveURLAsync(new Regex(".*/dashboard$", RegexOptions.IgnoreCase));

        // Create a plugin
        var slug = "logo-test-" + PlaywrightTester.GetRandomUInt256()[..8];
        await t.VerifyUserAccounts(user);
        await t.GoToUrl("/plugins/create");
        await t.Page!.FillAsync("#PluginSlug", slug);
        await t.Page!.FillAsync("#PluginTitle", "Logo Test Plugin");
        await t.Page!.FillAsync("#Description", "Testing logo upload");
        await t.Page.ClickAsync("#Create");
        await t.AssertNoError();

        // Navigate to plugin settings
        await t.GoToUrl($"/plugins/{slug}/settings");
        await t.AssertNoError();

        // Verify we're on the settings page
        _log.Log(LogLevel.Information, $"Current URL: {t.Page!.Url}");
        await Expect(t.Page).ToHaveURLAsync(new Regex($"/plugins/{Regex.Escape(slug)}/settings", RegexOptions.IgnoreCase));

        // Create a test image file
        var testImagePath = Path.Combine(Path.GetTempPath(), "test-logo.png");
        t.CreateTestImage(testImagePath);

        try
        {
            await t.Page.FillAsync("#PluginTitle", "Logo Test Plugin Updated");
            await t.Page.FillAsync("#Description", "Testing logo upload with description");
            await t.Page.FillAsync("#GitRepository", "https://github.com/test/repo");

            var fileInput = t.Page.Locator("input[type='file'][name='Logo']");
            await fileInput.SetInputFilesAsync(testImagePath);
            await t.Page.Locator("button[type='submit'][form='plugin-setting-form']").ClickAsync();
            await t.AssertNoError();

            var allPlugins = await conn.QueryAsync<dynamic>("SELECT slug, settings FROM plugins");
            _log.Log(LogLevel.Information, $"All plugins in database: {string.Join(", ", allPlugins.Select(p => $"{p.slug}: {p.settings}"))}");

            // Verify logo was uploaded by checking the database (note: JSON keys are camelCase)
            var logoUrl = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT settings->>'logo' FROM plugins WHERE slug = @Slug",
                new { Slug = slug });

            Assert.NotNull(logoUrl);
            Assert.NotEmpty(logoUrl);

            // Verify the filename contains the slug and a GUID pattern
            // Format should be: {slug}-{guid}.png
            var fileName = Path.GetFileNameWithoutExtension(new Uri(logoUrl).LocalPath);
            Assert.StartsWith(slug, fileName);

            // Check that it contains a GUID-like pattern (32 hex chars or with hyphens)
            var guidPattern = new Regex(@"-[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}$", RegexOptions.IgnoreCase);
            Assert.Matches(guidPattern, fileName);

            // Verify file extension is preserved
            Assert.EndsWith(".png", logoUrl, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(testImagePath))
                File.Delete(testImagePath);
        }
    }
}
