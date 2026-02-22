using System;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit;
using Npgsql;
using PluginBuilder.DataModels;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using PluginBuilder.ViewModels.Admin;
using PluginBuilder.ViewModels.Plugin;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.PublicTests;

[Collection("Playwright Tests")]
public class PublicDirectoryUITests(ITestOutputHelper output) : PageTest
{
    private readonly XUnitLogger _log = new("PublicDirectoryUITests", output);

    [Fact]
    public async Task PublicDirectory_RespectsPluginVisibility()
    {
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        await tester.StartAsync();

        var conn = await tester.Server.GetService<DBConnectionFactory>().Open();

        var slug = new PluginSlug("rockstar-stylist");
        var slugString = slug.ToString();

        var ownerId = await tester.Server.CreateFakeUserAsync();
        var fullBuildId = await tester.Server.CreateAndBuildPluginAsync(ownerId);

        var manifestInfoJson = await conn.QuerySingleAsync<string>(
            "SELECT manifest_info FROM builds WHERE plugin_slug = @PluginSlug AND id = @BuildId",
            new { PluginSlug = slugString, fullBuildId.BuildId });
        var manifest = PluginManifest.Parse(manifestInfoJson);

        // Remove pre-release
        await conn.SetVersionBuild(fullBuildId, manifest.Version, manifest.BTCPayMinVersion, false);

        // Listed should be visible
        await conn.SetPluginSettings(slug, null, PluginVisibilityEnum.Listed);
        await tester.GoToUrl("/public/plugins");
        await tester.Page!.WaitForSelectorAsync("a[href='/public/plugins/rockstar-stylist']");
        Assert.True(await tester.Page.Locator("a[href='/public/plugins/rockstar-stylist']").IsVisibleAsync());

        // Plugin public page should be visible
        await tester.GoToUrl($"/public/plugins/{slugString}");
        var contentListed = await tester.Page.ContentAsync();
        Assert.Contains(slugString, contentListed, StringComparison.OrdinalIgnoreCase);

        // Plugin description with more than 300 characters should be truncated
        var longDescription = new string('a', 290) + " " + new string('b', 20);
        await conn.SetPluginSettings(slug, new PluginSettings { Description = longDescription, PluginTitle = "Rockstar Stylist" }, PluginVisibilityEnum.Listed);
        await tester.GoToUrl("/public/plugins");
        await tester.Page.WaitForSelectorAsync(".plugin-card");
        var displayedDesc = (await tester.Page.Locator(".plugin-card p").First.InnerTextAsync()).Trim();
        Assert.EndsWith("...", displayedDesc);
        Assert.True(displayedDesc.Length <= longDescription.Length);
        Assert.DoesNotContain(new string('b', 20), displayedDesc, StringComparison.Ordinal);

        // Unlisted shouldn't be visible
        await conn.SetPluginSettings(slug, null, PluginVisibilityEnum.Unlisted);
        await tester.GoToUrl("/public/plugins");
        await tester.Page.ReloadAsync();
        Assert.False(await tester.Page.Locator("a[href='/public/plugins/rockstar-stylist']").IsVisibleAsync());

        // Unlisted with search term should be visible
        await tester.Page.Locator("input[name='searchPluginName']").FillAsync("rockstar");
        await tester.Page.Keyboard.PressAsync("Enter");
        await tester.Page.WaitForSelectorAsync("a[href='/public/plugins/rockstar-stylist']",
            new PageWaitForSelectorOptions { State = WaitForSelectorState.Visible });
        Assert.True(await tester.Page.Locator("a[href='/public/plugins/rockstar-stylist']").IsVisibleAsync());

        // Author search should be visible
        await tester.GoToUrl("/public/plugins");
        await tester.Page.Locator("input[name='searchPluginName']").FillAsync("NicolasDorier");
        await tester.Page.Keyboard.PressAsync("Enter");
        await tester.Page.WaitForSelectorAsync("a[href='/public/plugins/rockstar-stylist']",
            new PageWaitForSelectorOptions { State = WaitForSelectorState.Visible });
        Assert.True(await tester.Page.Locator("a[href='/public/plugins/rockstar-stylist']").IsVisibleAsync());

        // Hidden shouldn't appear
        await conn.SetPluginSettings(slug, null, PluginVisibilityEnum.Hidden);
        await tester.GoToUrl("/public/plugins");
        await tester.Page.Locator("input[name='searchPluginName']").FillAsync("rockstar");
        await tester.Page.Keyboard.PressAsync("Enter");
        await tester.Page.WaitForSelectorAsync("a[href='/public/plugins/rockstar-stylist']",
            new PageWaitForSelectorOptions { State = WaitForSelectorState.Hidden });
        Assert.False(await tester.Page.Locator("a[href='/public/plugins/rockstar-stylist']").IsVisibleAsync());

        // Log in as plugin owner and access page again
        await tester.GoToUrl("/register");
        var email = await tester.RegisterNewUser();
        await tester.LogIn(email);
        var userId = await conn.QuerySingleAsync<string>(
            "SELECT \"Id\" FROM \"AspNetUsers\" WHERE \"Email\" = @Email", new { Email = email });
        await conn.AddUserPlugin(slug, userId);

        await tester.GoToUrl($"/public/plugins/{slugString}");
        var hiddenAlert = tester.Page.Locator("#hidden-plugin-alert");
        Assert.True(await hiddenAlert.IsVisibleAsync());

        //sort tests
        await conn.SetPluginSettings(slug, null, PluginVisibilityEnum.Listed);
        var reviewer1 = await tester.Server.CreateFakeUserAsync("sort-reviewer1@x.com");
        var reviewer2 = await tester.Server.CreateFakeUserAsync("sort-reviewer2@x.com");
        var reviewer3 = await tester.Server.CreateFakeUserAsync("sort-reviewer3@x.com");
        var reviewer4 = await tester.Server.CreateFakeUserAsync("sort-reviewer4@x.com");
        var reviewer5 = await tester.Server.CreateFakeUserAsync("sort-reviewer5@x.com");
        var reviewer6 = await tester.Server.CreateFakeUserAsync("sort-reviewer6@x.com");

        var popularSlug = new PluginSlug("public-directory-popular");
        var popularSlugString = popularSlug.ToString();
        var popularBuild = await tester.Server.CreateAndBuildPluginAsync(ownerId, popularSlugString);
        var popularManifestJson = await conn.QuerySingleAsync<string>("SELECT manifest_info FROM builds WHERE plugin_slug = @PluginSlug AND id = @BuildId",
            new { PluginSlug = popularSlugString, popularBuild.BuildId });
        var popularManifest = PluginManifest.Parse(popularManifestJson);
        await conn.SetVersionBuild(popularBuild, popularManifest.Version, popularManifest.BTCPayMinVersion, false);
        await conn.SetPluginSettings(popularSlug, null, PluginVisibilityEnum.Listed);

        // 2) sort=alpha
        const string updateTitleSql = """
                                      UPDATE plugins
                                      SET settings = COALESCE(settings, '{}'::jsonb) || jsonb_build_object('pluginTitle', @title)
                                      WHERE slug = @slug
                                      """;
        await conn.ExecuteAsync(updateTitleSql, new { slug = slugString, title = "Bob Plugin" });
        await conn.ExecuteAsync(updateTitleSql, new { slug = popularSlugString, title = "Alice Plugin" });
        var (rockstarAlpha, popularAlpha) = await GetIndexesAsync("?sort=alpha");
        Assert.True(popularAlpha < rockstarAlpha, "Expected 'Alice Plugin' to appear before 'Bob Plugin' in alpha sort.");

        // 3) sort=rating
        await SaveReviewData(conn, slugString, reviewer1);
        var (rockstarRating, popularRating) = await GetIndexesAsync("?sort=rating");
        Assert.True(rockstarRating < popularRating, "Expected plugin with rating to appear before plugin without rating in rating sort.");

        // 4) sort=recent
        var (rockstarRecent, popularRecent) = await GetIndexesAsync("?sort=recent");
        Assert.True(popularRecent < rockstarRecent, "Expected newer plugin to appear first in recent sort.");

        // 5) sort=smart
        await SaveReviewData(conn, slugString, reviewer2);
        await SaveReviewData(conn, popularSlugString, reviewer1);
        await SaveReviewData(conn, popularSlugString, reviewer2);
        await SaveReviewData(conn, popularSlugString, reviewer3);
        await SaveReviewData(conn, popularSlugString, reviewer4);
        await SaveReviewData(conn, popularSlugString, reviewer5);
        await SaveReviewData(conn, popularSlugString, reviewer6);
        var (rockstarSmart, popularSmart) = await GetIndexesAsync("?sort=smart");
        Assert.True(popularSmart < rockstarSmart, "Expected plugin with many reviews to appear first in smart sort.");

        const string setBuildDateSql = """
                                       UPDATE builds
                                       SET created_at = @date
                                       WHERE plugin_slug = @slug AND id = @buildId
                                       """;
        var oldDate = DateTimeOffset.UtcNow.AddDays(-60);
        await conn.ExecuteAsync(setBuildDateSql, new { slug = popularSlugString, buildId = popularBuild.BuildId, date = oldDate });
        var (rockstarSmart2, popularSmart2) = await GetIndexesAsync("?sort=smart");
        Assert.True(rockstarSmart2 < popularSmart2, "Expected the newer to appear first in smart sort.");

        return;

        async Task<(int rockstarIndex, int popularIndex)> GetIndexesAsync(string query)
        {
            await tester.GoToUrl("/public/plugins" + query);

            var links = tester.Page.Locator(".plugin-card a[href^='/public/plugins/']");
            var count = await links.CountAsync();
            Assert.True(count >= 2, "Expected at least 2 plugins in directory.");

            var rockstarIndex = -1;
            var popularIndex = -1;

            for (var i = 0; i < count; i++)
            {
                var href = await links.Nth(i).GetAttributeAsync("href");
                if (href == $"/public/plugins/{slugString}")
                    rockstarIndex = i;
                else if (href == $"/public/plugins/{popularSlugString}")
                    popularIndex = i;
            }

            Assert.True(rockstarIndex >= 0, $"{slugString} not found.");
            Assert.True(popularIndex >= 0, $"{popularSlugString} not found.");

            return (rockstarIndex, popularIndex);
        }
    }

    private async Task SaveReviewData(NpgsqlConnection conn, string pluginSlug, string userId)
    {
        var reviewerAccountDetails = await conn.GetAccountDetailSettings(userId);
        var importModel = new ImportReviewViewModel
        {
            SelectedUserId = userId,
            LinkExistingUser = true,
            ReviewerName = reviewerAccountDetails?.Github ?? "test-reviewer",
            ReviewerProfileUrl = reviewerAccountDetails?.Github is { Length: > 0 }
                ? $"https://github.com/{reviewerAccountDetails.Github.Trim().TrimStart('@').Trim('/')}"
                : null,
            ReviewerAvatarUrl = null
        };
        PluginReviewViewModel reviewViewModel = new()
        {
            PluginSlug = pluginSlug,
            UserId = userId,
            Rating = 5,
            Body = "This is a good plugin"
        };
        reviewViewModel.ReviewerId = await conn.CreateOrUpdatePluginReviewer(importModel);
        await conn.UpsertPluginReview(reviewViewModel);
    }
}
