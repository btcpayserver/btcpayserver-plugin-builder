using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright.Xunit;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.DataModels;
using PluginBuilder.Services;
using PluginBuilder.Util;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.PluginTests;

[Collection("Playwright Tests")]
public class VideoUITests(ITestOutputHelper output) : PageTest
{
    private readonly XUnitLogger _log = new("VideoUrlUITests", output);

    [Fact]
    public async Task Owner_Can_Add_VideoUrl_And_It_Renders_On_PluginDetails()
    {
        await using var t = new PlaywrightTester(_log);
        t.Server.ReuseDatabase = false;
        await t.StartAsync();
        await using var conn = await t.Server.GetService<DBConnectionFactory>().Open();

        // Create user and plugin with a released version
        await conn.SettingsSetAsync(SettingsKeys.VerifiedGithub, "true");
        var verfCache = t.Server.GetService<AdminSettingsCache>();
        await verfCache.RefreshAllAdminSettings(conn);

        await t.GoToUrl("/register");
        var user = await t.RegisterNewUser();
        await Expect(t.Page!).ToHaveURLAsync(new Regex(".*/dashboard$", RegexOptions.IgnoreCase));
        await t.VerifyUserAccounts(user);

        var pluginSlug = "video-test-" + PlaywrightTester.GetRandomUInt256()[..8];
        var fullBuildId = await t.Server.CreateAndBuildPluginAsync(
            await conn.QuerySingleAsync<string>("SELECT \"Id\" FROM \"AspNetUsers\" WHERE \"Email\" = @Email", new { Email = user }),
            pluginSlug);

        // Release the build so plugin appears on public page
        var manifestInfoJson = await conn.QuerySingleAsync<string>(
            "SELECT manifest_info FROM builds WHERE plugin_slug = @PluginSlug AND id = @BuildId",
            new { PluginSlug = pluginSlug, fullBuildId.BuildId });
        var manifest = PluginManifest.Parse(manifestInfoJson);
        await conn.SetVersionBuild(fullBuildId, manifest.Version, manifest.BTCPayMinVersion, false);
        await conn.SetPluginSettings(pluginSlug, null, PluginVisibilityEnum.Listed);

        // Navigate to plugin settings and add YouTube video URL
        await t.GoToUrl($"/plugins/{pluginSlug}/settings");
        await Expect(t.Page).ToHaveURLAsync(new Regex($"/plugins/{Regex.Escape(pluginSlug)}/settings", RegexOptions.IgnoreCase));

        const string youtubeUrl = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";
        var videoUrlInput = t.Page.Locator("input[name='VideoUrl']");
        await Expect(videoUrlInput).ToBeVisibleAsync();
        await videoUrlInput.FillAsync(youtubeUrl);
        await t.Page.Locator("button[type='submit'][form='plugin-setting-form']").ClickAsync();
        await t.AssertNoError();

        // Verify VideoUrl was saved to database
        var savedVideoUrl = await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT settings->>'videoUrl' FROM plugins WHERE slug = @Slug",
            new { Slug = pluginSlug });
        Assert.Equal(youtubeUrl, savedVideoUrl);

        // Navigate to public plugin details page
        await t.GoToUrl($"/public/plugins/{pluginSlug}");
        await t.AssertNoError();

        // Verify video iframe is rendered with correct embed URL
        var videoIframe = t.Page.Locator("iframe[src*='youtube.com/embed']");
        await Expect(videoIframe).ToBeVisibleAsync();

        var iframeSrc = await videoIframe.GetAttributeAsync("src");
        Assert.NotNull(iframeSrc);
        Assert.Contains("youtube.com/embed/dQw4w9WgXcQ", iframeSrc);

        // Verify iframe has proper attributes for responsive design
        await Expect(videoIframe).ToHaveAttributeAsync("width", "100%");
        await Expect(videoIframe).ToHaveAttributeAsync("allowfullscreen", "");

        // Verify video container has rounded border styling
        var videoContainer = t.Page.Locator(".ratio.ratio-16x9");
        await Expect(videoContainer).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Owner_Can_Update_VideoUrl_From_YouTube_To_Vimeo()
    {
        await using var t = new PlaywrightTester(_log);
        t.Server.ReuseDatabase = false;
        await t.StartAsync();
        await using var conn = await t.Server.GetService<DBConnectionFactory>().Open();

        // Setup
        await conn.SettingsSetAsync(SettingsKeys.VerifiedGithub, "true");
        var verfCache = t.Server.GetService<AdminSettingsCache>();
        await verfCache.RefreshAllAdminSettings(conn);

        await t.GoToUrl("/register");
        var user = await t.RegisterNewUser();
        await t.VerifyUserAccounts(user);

        var pluginSlug = "video-update-" + PlaywrightTester.GetRandomUInt256()[..8];
        var userId = await conn.QuerySingleAsync<string>("SELECT \"Id\" FROM \"AspNetUsers\" WHERE \"Email\" = @Email", new { Email = user });
        var fullBuildId = await t.Server.CreateAndBuildPluginAsync(userId, pluginSlug);

        // Release and list the plugin
        var manifestInfoJson = await conn.QuerySingleAsync<string>(
            "SELECT manifest_info FROM builds WHERE plugin_slug = @PluginSlug AND id = @BuildId",
            new { PluginSlug = pluginSlug, fullBuildId.BuildId });
        var manifest = PluginManifest.Parse(manifestInfoJson);
        await conn.SetVersionBuild(fullBuildId, manifest.Version, manifest.BTCPayMinVersion, false);
        await conn.SetPluginSettings(pluginSlug, null, PluginVisibilityEnum.Listed);

        // Add initial YouTube URL
        await t.GoToUrl($"/plugins/{pluginSlug}/settings");
        const string youtubeUrl = "https://youtu.be/dQw4w9WgXcQ";
        await t.Page.Locator("input[name='VideoUrl']").FillAsync(youtubeUrl);
        await t.Page.Locator("button[type='submit'][form='plugin-setting-form']").ClickAsync();
        await t.AssertNoError();

        // Update to Vimeo URL
        await t.GoToUrl($"/plugins/{pluginSlug}/settings");
        const string vimeoUrl = "https://vimeo.com/123456789";
        var videoUrlInput = t.Page.Locator("input[name='VideoUrl']");
        await Expect(videoUrlInput).ToHaveValueAsync(youtubeUrl);
        await videoUrlInput.FillAsync(vimeoUrl);
        await t.Page.Locator("button[type='submit'][form='plugin-setting-form']").ClickAsync();
        await t.AssertNoError();

        // Verify Vimeo URL was saved
        var savedVideoUrl = await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT settings->>'videoUrl' FROM plugins WHERE slug = @Slug",
            new { Slug = pluginSlug });
        Assert.Equal(vimeoUrl, savedVideoUrl);

        // Verify Vimeo iframe renders on public page
        await t.GoToUrl($"/public/plugins/{pluginSlug}");
        var vimeoIframe = t.Page.Locator("iframe[src*='player.vimeo.com/video']");
        await Expect(vimeoIframe).ToBeVisibleAsync();

        var iframeSrc = await vimeoIframe.GetAttributeAsync("src");
        Assert.NotNull(iframeSrc);
        Assert.Contains("player.vimeo.com/video/123456789", iframeSrc);
    }

    [Fact]
    public async Task Owner_Can_Remove_VideoUrl()
    {
        await using var t = new PlaywrightTester(_log);
        t.Server.ReuseDatabase = false;
        await t.StartAsync();
        await using var conn = await t.Server.GetService<DBConnectionFactory>().Open();

        // Setup
        await conn.SettingsSetAsync(SettingsKeys.VerifiedGithub, "true");
        var verfCache = t.Server.GetService<AdminSettingsCache>();
        await verfCache.RefreshAllAdminSettings(conn);

        await t.GoToUrl("/register");
        var user = await t.RegisterNewUser();
        await t.VerifyUserAccounts(user);

        var pluginSlug = "video-remove-" + PlaywrightTester.GetRandomUInt256()[..8];
        var userId = await conn.QuerySingleAsync<string>("SELECT \"Id\" FROM \"AspNetUsers\" WHERE \"Email\" = @Email", new { Email = user });
        var fullBuildId = await t.Server.CreateAndBuildPluginAsync(userId, pluginSlug);

        // Release and list the plugin
        var manifestInfoJson = await conn.QuerySingleAsync<string>(
            "SELECT manifest_info FROM builds WHERE plugin_slug = @PluginSlug AND id = @BuildId",
            new { PluginSlug = pluginSlug, fullBuildId.BuildId });
        var manifest = PluginManifest.Parse(manifestInfoJson);
        await conn.SetVersionBuild(fullBuildId, manifest.Version, manifest.BTCPayMinVersion, false);
        await conn.SetPluginSettings(pluginSlug, null, PluginVisibilityEnum.Listed);

        // Add video URL
        await t.GoToUrl($"/plugins/{pluginSlug}/settings");
        await t.Page.Locator("input[name='VideoUrl']").FillAsync("https://www.youtube.com/watch?v=test123");
        await t.Page.Locator("button[type='submit'][form='plugin-setting-form']").ClickAsync();
        await t.AssertNoError();

        // Verify video is rendered
        await t.GoToUrl($"/public/plugins/{pluginSlug}");
        await Expect(t.Page.Locator("iframe[src*='youtube.com/embed']")).ToBeVisibleAsync();

        // Remove video URL (clear the field)
        await t.GoToUrl($"/plugins/{pluginSlug}/settings");
        var videoUrlInput = t.Page.Locator("input[name='VideoUrl']");
        await videoUrlInput.FillAsync("");
        await t.Page.Locator("button[type='submit'][form='plugin-setting-form']").ClickAsync();
        await t.AssertNoError();

        // Verify VideoUrl was removed from database
        var savedVideoUrl = await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT settings->>'videoUrl' FROM plugins WHERE slug = @Slug",
            new { Slug = pluginSlug });
        Assert.True(string.IsNullOrEmpty(savedVideoUrl));

        // Verify video iframe is NOT rendered on public page
        await t.GoToUrl($"/public/plugins/{pluginSlug}");
        var videoIframe = t.Page.Locator("iframe[src*='youtube.com/embed'], iframe[src*='player.vimeo.com']");
        await Expect(videoIframe).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task VideoUrl_Validation_Rejects_Invalid_URLs()
    {
        await using var t = new PlaywrightTester(_log);
        t.Server.ReuseDatabase = false;
        await t.StartAsync();
        await using var conn = await t.Server.GetService<DBConnectionFactory>().Open();

        // Setup
        await conn.SettingsSetAsync(SettingsKeys.VerifiedGithub, "true");
        var verfCache = t.Server.GetService<AdminSettingsCache>();
        await verfCache.RefreshAllAdminSettings(conn);

        await t.GoToUrl("/register");
        var user = await t.RegisterNewUser();
        await t.VerifyUserAccounts(user);

        var pluginSlug = "video-validation-" + PlaywrightTester.GetRandomUInt256()[..8];
        await t.GoToUrl("/plugins/create");
        await t.Page.FillAsync("#PluginSlug", pluginSlug);
        await t.Page.FillAsync("#PluginTitle", pluginSlug);
        await t.Page.FillAsync("#Description", "Test");
        await t.Page.ClickAsync("#Create");
        await t.AssertNoError();

        // Test invalid URL format
        await t.GoToUrl($"/plugins/{pluginSlug}/settings");
        await t.Page.Locator("input[name='VideoUrl']").FillAsync("not-a-valid-url");
        await t.Page.Locator("button[type='submit'][form='plugin-setting-form']").ClickAsync();

        var errorMessage = t.Page.Locator(".field-validation-error, .validation-summary-errors, .alert-danger");
        await Expect(errorMessage).ToBeVisibleAsync();
        await Expect(errorMessage).ToContainTextAsync(new Regex("valid url", RegexOptions.IgnoreCase));

        // Test unsupported platform (e.g., Dailymotion)
        await t.GoToUrl($"/plugins/{pluginSlug}/settings");
        await t.Page.Locator("input[name='VideoUrl']").FillAsync("https://www.dailymotion.com/video/x8abc123");
        await t.Page.Locator("button[type='submit'][form='plugin-setting-form']").ClickAsync();

        var platformError = t.Page.Locator(".field-validation-error, .validation-summary-errors, .alert-danger");
        await Expect(platformError).ToBeVisibleAsync();
        await Expect(platformError).ToContainTextAsync(new Regex("supported.*platform|youtube|vimeo", RegexOptions.IgnoreCase));
    }

    [Fact]
    public async Task Admin_Can_Edit_VideoUrl_For_Any_Plugin()
    {
        await using var t = new PlaywrightTester(_log);
        t.Server.ReuseDatabase = false;
        await t.StartAsync();
        await using var conn = await t.Server.GetService<DBConnectionFactory>().Open();

        // Create plugin owner
        var ownerId = await t.Server.CreateFakeUserAsync("owner@videotest.com", confirmEmail: true, githubVerified: true);
        var pluginSlug = "admin-video-" + PlaywrightTester.GetRandomUInt256()[..8];
        var fullBuildId = await t.Server.CreateAndBuildPluginAsync(ownerId, pluginSlug);

        // Release and list the plugin
        var manifestInfoJson = await conn.QuerySingleAsync<string>(
            "SELECT manifest_info FROM builds WHERE plugin_slug = @PluginSlug AND id = @BuildId",
            new { PluginSlug = pluginSlug, fullBuildId.BuildId });
        var manifest = PluginManifest.Parse(manifestInfoJson);
        await conn.SetVersionBuild(fullBuildId, manifest.Version, manifest.BTCPayMinVersion, false);
        await conn.SetPluginSettings(pluginSlug, null, PluginVisibilityEnum.Listed);

        // Create and login as admin
        var adminEmail = await CreateServerAdminAsync(t);
        await t.LogIn(adminEmail);

        // Navigate to admin plugin edit page
        await t.GoToUrl($"/admin/plugins/edit/{pluginSlug}");
        await Expect(t.Page).ToHaveURLAsync(new Regex($"/admin/plugins/edit/{Regex.Escape(pluginSlug)}$", RegexOptions.IgnoreCase));

        // Add video URL as admin
        const string youtubeUrl = "https://www.youtube.com/watch?v=admin_test_123";
        var videoUrlInput = t.Page.Locator("input[name='PluginSettings.VideoUrl']");
        await Expect(videoUrlInput).ToBeVisibleAsync();
        await videoUrlInput.FillAsync(youtubeUrl);
        await t.Page.Locator("button[type='submit']").ClickAsync();
        await t.AssertNoError();

        // Verify VideoUrl was saved
        var savedVideoUrl = await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT settings->>'videoUrl' FROM plugins WHERE slug = @Slug",
            new { Slug = pluginSlug });
        Assert.Equal(youtubeUrl, savedVideoUrl);

        // Verify it renders on public page
        await t.GoToUrl($"/public/plugins/{pluginSlug}");
        var videoIframe = t.Page.Locator("iframe[src*='youtube.com/embed']");
        await Expect(videoIframe).ToBeVisibleAsync();
    }

    [Fact]
    public async Task VideoUrl_Supports_Various_YouTube_URL_Formats()
    {
        await using var t = new PlaywrightTester(_log);
        t.Server.ReuseDatabase = false;
        await t.StartAsync();
        await using var conn = await t.Server.GetService<DBConnectionFactory>().Open();

        // Setup
        await conn.SettingsSetAsync(SettingsKeys.VerifiedGithub, "true");
        var verfCache = t.Server.GetService<AdminSettingsCache>();
        await verfCache.RefreshAllAdminSettings(conn);

        await t.GoToUrl("/register");
        var user = await t.RegisterNewUser();
        await t.VerifyUserAccounts(user);

        var userId = await conn.QuerySingleAsync<string>("SELECT \"Id\" FROM \"AspNetUsers\" WHERE \"Email\" = @Email", new { Email = user });

        // Test different YouTube URL formats
        var testCases = new[]
        {
            new { Format = "Standard", Url = "https://www.youtube.com/watch?v=abc123xyz", ExpectedVideoId = "abc123xyz" },
            new { Format = "Short", Url = "https://youtu.be/def456uvw", ExpectedVideoId = "def456uvw" },
            new { Format = "With timestamp", Url = "https://www.youtube.com/watch?v=ghi789rst&t=30s", ExpectedVideoId = "ghi789rst" }
        };

        foreach (var testCase in testCases)
        {
            var pluginSlug = $"yt-format-{testCase.Format.ToLowerInvariant().Replace(" ", "-")}-{PlaywrightTester.GetRandomUInt256()[..6]}";
            var fullBuildId = await t.Server.CreateAndBuildPluginAsync(userId, pluginSlug);

            // Release and list
            var manifestInfoJson = await conn.QuerySingleAsync<string>(
                "SELECT manifest_info FROM builds WHERE plugin_slug = @PluginSlug AND id = @BuildId",
                new { PluginSlug = pluginSlug, fullBuildId.BuildId });
            var manifest = PluginManifest.Parse(manifestInfoJson);
            await conn.SetVersionBuild(fullBuildId, manifest.Version, manifest.BTCPayMinVersion, false);
            await conn.SetPluginSettings(pluginSlug, null, PluginVisibilityEnum.Listed);

            // Add video URL
            await t.GoToUrl($"/plugins/{pluginSlug}/settings");
            await t.Page.Locator("input[name='VideoUrl']").FillAsync(testCase.Url);
            await t.Page.Locator("button[type='submit'][form='plugin-setting-form']").ClickAsync();
            await t.AssertNoError();

            // Verify correct embed URL is generated
            await t.GoToUrl($"/public/plugins/{pluginSlug}");
            var videoIframe = t.Page.Locator("iframe[src*='youtube.com/embed']");
            await Expect(videoIframe).ToBeVisibleAsync();

            var iframeSrc = await videoIframe.GetAttributeAsync("src");
            Assert.NotNull(iframeSrc);
            Assert.Contains($"youtube.com/embed/{testCase.ExpectedVideoId}", iframeSrc);
        }
    }

    [Fact]
    public async Task VideoUrl_Does_Not_Display_When_Plugin_Has_No_Video()
    {
        await using var t = new PlaywrightTester(_log);
        t.Server.ReuseDatabase = false;
        await t.StartAsync();
        await using var conn = await t.Server.GetService<DBConnectionFactory>().Open();

        // Create plugin without video URL
        var ownerId = await t.Server.CreateFakeUserAsync(confirmEmail: true, githubVerified: true);
        var pluginSlug = "no-video-" + PlaywrightTester.GetRandomUInt256()[..8];
        var fullBuildId = await t.Server.CreateAndBuildPluginAsync(ownerId, pluginSlug);

        // Release and list the plugin
        var manifestInfoJson = await conn.QuerySingleAsync<string>(
            "SELECT manifest_info FROM builds WHERE plugin_slug = @PluginSlug AND id = @BuildId",
            new { PluginSlug = pluginSlug, fullBuildId.BuildId });
        var manifest = PluginManifest.Parse(manifestInfoJson);
        await conn.SetVersionBuild(fullBuildId, manifest.Version, manifest.BTCPayMinVersion, false);
        await conn.SetPluginSettings(pluginSlug, null, PluginVisibilityEnum.Listed);

        // Navigate to public plugin page
        await t.GoToUrl($"/public/plugins/{pluginSlug}");
        await t.AssertNoError();

        // Verify no video iframe is rendered
        var videoIframe = t.Page.Locator("iframe[src*='youtube.com/embed'], iframe[src*='player.vimeo.com']");
        await Expect(videoIframe).ToHaveCountAsync(0);

        // Verify video section/container is not visible
        var videoContainer = t.Page.Locator(".ratio.ratio-16x9");
        await Expect(videoContainer).ToHaveCountAsync(0);
    }

    private static async Task<string> CreateServerAdminAsync(PlaywrightTester tester)
    {
        using var scope = tester.Server.WebApp.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

        if (!await roleManager.RoleExistsAsync(Roles.ServerAdmin))
            await roleManager.CreateAsync(new IdentityRole(Roles.ServerAdmin));

        var email = $"admin-video-{Guid.NewGuid():N}@test.com";
        const string password = "123456";
        var user = new IdentityUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true
        };

        var result = await userManager.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            throw new InvalidOperationException($"Failed to create admin user: {errors}");
        }

        await userManager.AddToRoleAsync(user, Roles.ServerAdmin);
        return email;
    }
}
