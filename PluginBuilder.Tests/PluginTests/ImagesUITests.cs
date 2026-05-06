using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Playwright.Xunit;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.DataModels;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.PluginTests;

[Collection("Playwright Tests")]
public class ImagesUITests(ITestOutputHelper output) : PageTest
{
    private readonly XUnitLogger _log = new("ImagesUITests", output);

    [Fact]
    public async Task SettingsCanAddRemoveAndKeepNewImagesFirst()
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
        await t.VerifyUserAccounts(user);

        var pluginSlug = "images-settings-" + PlaywrightTester.GetRandomUInt256()[..8];
        var userId = await conn.QuerySingleAsync<string>("SELECT \"Id\" FROM \"AspNetUsers\" WHERE \"Email\" = @Email", new { Email = user });
        await t.Server.CreateAndBuildPluginAsync(userId, pluginSlug);

        var old1 = "https://example.com/old-1.png";
        var old2 = "https://example.com/old-2.png";
        await conn.SetPluginSettings(pluginSlug, new PluginSettings
        {
            PluginTitle = pluginSlug,
            Description = "Images settings test",
            GitRepository = ServerTester.RepoUrl,
            Images = [old1, old2]
        });

        await t.GoToUrl($"/plugins/{pluginSlug}/settings");
        await Expect(t.Page!.Locator("#images-order-list [data-image-item]")).ToHaveCountAsync(2);

        var files = CreateTempImages(t, 2, "settings-images");
        try
        {
            await t.Page.Locator("#images-input").SetInputFilesAsync(files);
            await Expect(t.Page.Locator("#images-order-list [data-image-item]")).ToHaveCountAsync(4);

            var firstNewCard = t.Page.Locator("#images-order-list [data-image-item][data-new-id]").First;
            var newId = await firstNewCard.GetAttributeAsync("data-new-id");
            Assert.False(string.IsNullOrWhiteSpace(newId));

            var old1Card = t.Page.Locator($"#images-order-list [data-existing-input][value='{old1}']").Locator("xpath=ancestor::*[@data-image-item][1]");
            await old1Card.Locator("button[name='removeImageUrl']").ClickAsync();
            await t.AssertNoError();

            var savedImages = await conn.QuerySingleAsync<string[]>(
                "SELECT COALESCE(ARRAY(SELECT jsonb_array_elements_text(settings->'images')), ARRAY[]::text[]) FROM plugins WHERE slug = @Slug",
                new { Slug = pluginSlug });

            Assert.Equal(3, savedImages.Length);
            Assert.DoesNotContain(old1, savedImages);
            Assert.Equal(old2, savedImages[^1]);
        }
        finally
        {
            DeleteFiles(files);
        }
    }

    [Fact]
    public async Task PluginDetailsMediaCarouselNavigatesBetweenVideoAndImages()
    {
        await using var t = new PlaywrightTester(_log);
        t.Server.ReuseDatabase = false;
        await t.StartAsync();
        await using var conn = await t.Server.GetService<DBConnectionFactory>().Open();

        var ownerId = await t.Server.CreateFakeUserAsync(confirmEmail: true, githubVerified: true);
        var pluginSlug = "images-carousel-" + PlaywrightTester.GetRandomUInt256()[..8];
        var fullBuildId = await t.Server.CreateAndBuildPluginAsync(ownerId, pluginSlug);

        var manifestInfoJson = await conn.QuerySingleAsync<string>(
            "SELECT manifest_info FROM builds WHERE plugin_slug = @PluginSlug AND id = @BuildId",
            new { PluginSlug = pluginSlug, fullBuildId.BuildId });
        var manifest = PluginManifest.Parse(manifestInfoJson);
        await conn.SetVersionBuild(fullBuildId, manifest.Version, manifest.BTCPayMinVersion, manifest.BTCPayMaxVersion, false);

        const string image1 = "https://example.com/carousel-1.png";
        const string image2 = "https://example.com/carousel-2.png";
        await conn.SetPluginSettings(pluginSlug, new PluginSettings
        {
            PluginTitle = pluginSlug,
            Description = "Carousel test",
            GitRepository = ServerTester.RepoUrl,
            VideoUrl = "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            Images = [image1, image2]
        }, PluginVisibilityEnum.Listed);

        await t.GoToUrl($"/public/plugins/{pluginSlug}");
        await t.AssertNoError();

        var thumbs = t.Page!.Locator("#plugin-media-carousel [data-media-thumb]");
        await Expect(thumbs).ToHaveCountAsync(3);
        await Expect(thumbs.First).ToHaveClassAsync(new Regex("is-active"));

        await thumbs.Nth(2).ClickAsync();
        await Expect(thumbs.Nth(2)).ToHaveClassAsync(new Regex("is-active"));
        await Expect(t.Page.Locator("#plugin-media-carousel .plugin-media-slide.is-active img")).ToHaveAttributeAsync("src", image2);

        await t.Page.Locator("#plugin-media-carousel [data-media-nav='prev']").ClickAsync();
        await Expect(t.Page.Locator("#plugin-media-carousel .plugin-media-slide.is-active img")).ToHaveAttributeAsync("src", image1);
    }

    private static string[] CreateTempImages(PlaywrightTester tester, int count, string prefix)
    {
        var result = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}.png");
            tester.CreateTestImage(path);
            result.Add(path);
        }

        return result.ToArray();
    }

    private static void DeleteFiles(IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            try
            {
                if (File.Exists(file))
                    File.Delete(file);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

}
