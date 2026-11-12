using Dapper;
using Newtonsoft.Json;
using PluginBuilder.APIModels;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.DataModels;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.ApiTests;

public class PluginSearchApiTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    [Theory]
    [InlineData("2.3.7", false)]
    [InlineData("2.3.7", true)]
    [InlineData("1.9.9.9", false)]
    [InlineData(null, false)]
    public async Task SearchesRequireExactUnlistedNamesAndPreserveLegacyBrowsing(string? hostVersion, bool allVersions)
    {
        await using var tester = Create();
        tester.ReuseDatabase = false;
        await tester.Start();
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.SettingsSetAsync(SettingsKeys.RateLimitPermitLimit, "100");
        await tester.GetService<AdminSettingsCache>().RefreshRateLimitSettings(conn);
        var ownerId = await tester.CreateFakeUserAsync();
        var slug = $"search-{Guid.NewGuid():N}"[..18];
        var lookalike = slug + "-extra";
        var listed = slug + "-listed";
        var hidden = slug + "-hidden";
        const string title = "Shared Café Directory Title 100%_\\";
        const string listedTitle = "Listed " + title;
        foreach (var (pluginSlug, visibility, pluginTitle) in new[]
                 {
                     (slug, PluginVisibilityEnum.Unlisted, title),
                     (lookalike, PluginVisibilityEnum.Unlisted, (string?)null),
                     (listed, PluginVisibilityEnum.Listed, listedTitle),
                     (hidden, PluginVisibilityEnum.Hidden, "Hidden " + title)
                 })
        {
            var build = await tester.CreatePublishedPluginAsync(ownerId, pluginSlug);
            var json = await conn.QuerySingleAsync<string>(
                "SELECT manifest_info FROM builds WHERE plugin_slug = @pluginSlug AND id = @id",
                new { pluginSlug, id = build.BuildId });
            var manifest = PluginManifest.Parse(json);
            await conn.SetVersionBuild(build, manifest.Version, manifest.BTCPayMinVersion, manifest.BTCPayMaxVersion, false);
            await conn.SetPluginSettings(pluginSlug, new PluginSettings
            {
                PluginTitle = pluginTitle is null ? null : $"  {pluginTitle}  "
            }, visibility);
        }

        var identifier = await conn.QuerySingleAsync<string>("SELECT identifier FROM plugins WHERE slug = @slug", new { slug });
        var hiddenIdentifier = await conn.QuerySingleAsync<string>("SELECT identifier FROM plugins WHERE slug = @hidden", new { hidden });
        using var client = tester.CreateHttpClient();
        foreach (var search in new[]
                 {
                     identifier, slug, $"  {slug}  ", $"  {identifier}  ",
                     title, title.ToUpperInvariant(), $"  {title.ToLowerInvariant()}  "
                 })
        {
            var matches = await Search(search);
            Assert.Contains(matches, p => p.ProjectSlug == slug);
            Assert.DoesNotContain(matches, p => p.ProjectSlug == lookalike || p.ProjectSlug == hidden);
        }

        foreach (var search in new[]
                 {
                     slug[..^1], slug.ToUpperInvariant(), identifier.ToLowerInvariant(),
                     "rockstar", "Shared", "CAFÉ", listedTitle.ToUpperInvariant(),
                     title[..^1], title + "%", title.Replace(' ', '_'), title.Replace(" ", "  "),
                     "%", "_", identifier + "%", hidden, hiddenIdentifier,
                     "Hidden " + title, ("Hidden " + title).ToUpperInvariant(), $"  hidden {title.ToLowerInvariant()}  "
                 })
        {
            var matches = await Search(search);
            Assert.DoesNotContain(matches, p => p.ProjectSlug == slug || p.ProjectSlug == lookalike || p.ProjectSlug == hidden);
            if (search == "Shared" || search == "CAFÉ" || search == listedTitle.ToUpperInvariant())
                Assert.Contains(matches, p => p.ProjectSlug == listed);
        }

        // Manifest metadata cannot supply a public title or make a plugin match a title search.
        foreach (var search in new[] { "Rockstar Stylist", "ROCKSTAR STYLIST", "  rockstar stylist  " })
        {
            var matches = await Search(search);
            Assert.Empty(matches);
        }

        var legacy = Assert.Single(await Search(lookalike), p => p.ProjectSlug == lookalike);
        Assert.Null(legacy.PluginTitle);
        Assert.Equal("Rockstar Stylist", legacy.ManifestInfo["Name"]?.ToString());
        var browsing = await Search(null);
        Assert.Contains(browsing, p => p.ProjectSlug == listed);
        Assert.DoesNotContain(browsing, p => p.ProjectSlug == hidden);
        Assert.Equal(hostVersion is null or "1.9.9.9", browsing.Any(p => p.ProjectSlug == slug));

        async Task<PublishedVersion[]> Search(string? search)
        {
            var path = $"/api/v1/plugins?includeAllVersions={allVersions.ToString().ToLowerInvariant()}";
            if (hostVersion is not null) path += $"&btcpayVersion={hostVersion}";
            if (search is not null) path += $"&searchPluginName={Uri.EscapeDataString(search)}";
            return JsonConvert.DeserializeObject<PublishedVersion[]>(await client.GetStringAsync(path))!;
        }
    }
}
