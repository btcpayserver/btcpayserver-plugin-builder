using Dapper;
using Newtonsoft.Json;
using PluginBuilder.APIModels;
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
        var ownerId = await tester.CreateFakeUserAsync();
        var slug = $"search-{Guid.NewGuid():N}"[..18];
        var lookalike = slug + "-extra";
        var listed = slug + "-listed";
        var hidden = slug + "-hidden";
        const string title = "Shared Directory Title 100%_\\";
        foreach (var (pluginSlug, visibility, pluginTitle) in new[]
                 {
                     (slug, PluginVisibilityEnum.Unlisted, title),
                     (lookalike, PluginVisibilityEnum.Unlisted, (string?)null),
                     (listed, PluginVisibilityEnum.Listed, title),
                     (hidden, PluginVisibilityEnum.Hidden, title)
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
                     "rockstar", "Shared", title[..^1], title + "%", title.Replace(' ', '_'), title.Replace(" ", "  "),
                     "%", "_", identifier + "%", hidden, hiddenIdentifier
                 })
        {
            var matches = await Search(search);
            Assert.DoesNotContain(matches, p => p.ProjectSlug == slug || p.ProjectSlug == lookalike || p.ProjectSlug == hidden);
            if (search == "rockstar")
                Assert.Contains(matches, p => p.ProjectSlug == listed);
        }

        // Unlisted plugins fall back to the manifest name only when no custom title is set.
        foreach (var search in new[] { "Rockstar Stylist", "ROCKSTAR STYLIST", "  rockstar stylist  " })
        {
            var matches = await Search(search);
            Assert.Contains(matches, p => p.ProjectSlug == lookalike);
            Assert.DoesNotContain(matches, p => p.ProjectSlug == slug || p.ProjectSlug == hidden);
            Assert.Contains(matches, p => p.ProjectSlug == listed);
        }

        Assert.Contains(await Search(lookalike), p => p.ProjectSlug == lookalike);
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
