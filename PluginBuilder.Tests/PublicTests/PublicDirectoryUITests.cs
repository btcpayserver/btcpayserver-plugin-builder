using System;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
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

        const string slug = "rockstar-stylist";
        var ownerId = await tester.Server.CreateFakeUserAsync();
        var fullBuildId = await tester.Server.CreateAndBuildPluginAsync(ownerId);

        var manifestInfoJson = await conn.QuerySingleAsync<string>(
            "SELECT manifest_info FROM builds WHERE plugin_slug = @PluginSlug AND id = @BuildId",
            new { PluginSlug = slug, BuildId = fullBuildId.BuildId });
        var manifest = PluginManifest.Parse(manifestInfoJson);

        // Remove pre-release
        await conn.SetVersionBuild(fullBuildId, manifest.Version, manifest.BTCPayMinVersion, false);

        // Listed should be visible
        await conn.ExecuteAsync("UPDATE plugins SET visibility = 'listed' WHERE slug = @Slug", new { Slug = slug });
        await tester.GoToUrl("/public/plugins");
        await tester.Page!.WaitForSelectorAsync("a[href='/public/plugins/rockstar-stylist']");
        Assert.True(await tester.Page.Locator("a[href='/public/plugins/rockstar-stylist']").IsVisibleAsync());

        // Plugin public page should be visible
        await tester.GoToUrl($"/public/plugins/{slug}");
        var contentListed = await tester.Page.ContentAsync();
        Assert.Contains(slug, contentListed, StringComparison.OrdinalIgnoreCase);

        // Unlisted shouldn't be visible
        await conn.ExecuteAsync("UPDATE plugins SET visibility = 'unlisted' WHERE slug = @Slug", new { Slug = slug });
        await tester.GoToUrl("/public/plugins");

        // Unlisted with search term should be visible
        await tester.Page.Locator("input[name='searchPluginName']").FillAsync("rockstar");
        await tester.Page.Keyboard.PressAsync("Enter");
        await tester.Page.WaitForSelectorAsync("a[href='/public/plugins/rockstar-stylist']", new PageWaitForSelectorOptions { State = WaitForSelectorState.Visible });
        Assert.True(await tester.Page.Locator("a[href='/public/plugins/rockstar-stylist']").IsVisibleAsync());

        // Hidden shouldn't appear
        await conn.ExecuteAsync("UPDATE plugins SET visibility = 'hidden' WHERE slug = @Slug", new { Slug = slug });
        await tester.GoToUrl("/public/plugins");
        await tester.Page.Locator("input[name='searchPluginName']").FillAsync("rockstar");
        await tester.Page.Keyboard.PressAsync("Enter");
        await tester.Page.WaitForSelectorAsync("a[href='/public/plugins/rockstar-stylist']", new PageWaitForSelectorOptions { State = WaitForSelectorState.Hidden });
        Assert.False(await tester.Page.Locator("a[href='/public/plugins/rockstar-stylist']").IsVisibleAsync());

        // Log in as plugin owner and access page again
        await tester.GoToUrl("/register");
        var email = await tester.RegisterNewUser();
        await tester.LogIn(email);
        var userId = await conn.QuerySingleAsync<string>(
            "SELECT \"Id\" FROM \"AspNetUsers\" WHERE \"Email\" = @Email", new { Email = email });
        await conn.AddUserPlugin(slug, userId);

        await tester.GoToUrl($"/public/plugins/{slug}");
        var hiddenAlert = tester.Page.Locator("#hidden-plugin-alert");
        Assert.True(await hiddenAlert.IsVisibleAsync());
    }
}
