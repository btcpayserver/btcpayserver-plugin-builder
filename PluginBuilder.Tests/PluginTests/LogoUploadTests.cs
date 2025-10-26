using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
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
        var verfCache = t.Server.GetService<UserVerifiedCache>();
        await verfCache.RefreshAllUserVerifiedSettings(conn);

        // Register user
        await t.GoToUrl("/register");
        var user = await t.RegisterNewUser();
        await Expect(t.Page!).ToHaveURLAsync(new Regex(".*/dashboard$", RegexOptions.IgnoreCase));

        // Create a plugin
        var slug = "logo-test-" + PlaywrightTester.GetRandomUInt256()[..8];
        await t.VerifyEmailAndGithubAsync(user);
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
        CreateTestImage(testImagePath);

        try
        {
            // Fill in required fields (PluginTitle, Description, GitRepository are all required)
            await t.Page.FillAsync("#PluginTitle", "Logo Test Plugin Updated");
            await t.Page.FillAsync("#Description", "Testing logo upload with description");
            await t.Page.FillAsync("#GitRepository", "https://github.com/test/repo");

            // Upload logo
            var fileInput = t.Page.Locator("input[type='file'][name='Logo']");
            await fileInput.SetInputFilesAsync(testImagePath);

            // Submit the form using the specific button
            await t.Page.Locator("button[type='submit'][form='plugin-setting-form']").ClickAsync();

            // Check for validation errors on the page
            var validationErrors = await t.Page.Locator(".text-danger").AllInnerTextsAsync();
            if (validationErrors.Any())
            {
                _log.Log(LogLevel.Warning, $"Validation errors found: {string.Join(", ", validationErrors)}");
            }

            await t.AssertNoError();

            // Debug: Check what's in the database
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

    /// <summary>
    /// Creates a minimal valid PNG image file for testing
    /// </summary>
    private void CreateTestImage(string path)
    {
        // Create a minimal 1x1 PNG image
        byte[] pngData = new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // PNG signature
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, // IHDR chunk
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, // 1x1 dimensions
            0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
            0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41, // IDAT chunk
            0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
            0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00,
            0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, // IEND chunk
            0x42, 0x60, 0x82
        };

        File.WriteAllBytes(path, pngData);
    }
}
