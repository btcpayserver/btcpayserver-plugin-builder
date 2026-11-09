using Dapper;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.DataModels;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.PublicTests;

public class PublicDirectorySearchTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnlistedPluginsRequireAnExactSlugOrNamespace(bool embedded)
    {
        await using var tester = Create();
        tester.ReuseDatabase = false;
        await tester.Start();
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        // Allow the search matrix to grow without testing the request quota.
        await conn.SettingsSetAsync(SettingsKeys.RateLimitPermitLimit, "100");
        await tester.GetService<AdminSettingsCache>().RefreshRateLimitSettings(conn);
        var ownerId = await tester.CreateFakeUserAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var unlistedSlug = $"lookup-{suffix}";
        var lookalikeSlug = $"{unlistedSlug}-extra";
        var listedSlug = $"listed-{suffix}";
        var hiddenSlug = $"hidden-{suffix}";
        const string title = "Shared Directory Title";

        foreach (var (slug, visibility, pluginTitle) in new[]
                 {
                     (unlistedSlug, PluginVisibilityEnum.Unlisted, title),
                     (lookalikeSlug, PluginVisibilityEnum.Unlisted, unlistedSlug),
                     (listedSlug, PluginVisibilityEnum.Listed, title),
                     (hiddenSlug, PluginVisibilityEnum.Hidden, title)
                 })
        {
            var build = await tester.CreatePublishedPluginAsync(ownerId, slug);
            var manifestJson = await conn.QuerySingleAsync<string>(
                "SELECT manifest_info FROM builds WHERE plugin_slug = @slug AND id = @id",
                new { slug, id = build.BuildId });
            var manifest = PluginManifest.Parse(manifestJson);
            await conn.SetVersionBuild(build, manifest.Version, manifest.BTCPayMinVersion, manifest.BTCPayMaxVersion, false);
            await conn.SetPluginSettings(slug, new PluginSettings { PluginTitle = pluginTitle }, visibility);
        }

        var identifier = await conn.QuerySingleAsync<string>(
            "SELECT identifier FROM plugins WHERE slug = @unlistedSlug", new { unlistedSlug });
        var hiddenIdentifier = await conn.QuerySingleAsync<string>(
            "SELECT identifier FROM plugins WHERE slug = @hiddenSlug", new { hiddenSlug });
        using var client = tester.CreateHttpClient();

        foreach (var search in new[] { identifier, unlistedSlug, $"  {unlistedSlug}  ", $"  {identifier}  " })
        {
            var html = await Search(search);
            AssertPlugin(html, unlistedSlug, search, present: true);
            AssertPlugin(html, lookalikeSlug, search, present: false);
            AssertPlugin(html, hiddenSlug, search, present: false);
        }

        foreach (var search in new[]
                 {
                     "", "   ", unlistedSlug[..^1], unlistedSlug + "%",
                     "%", "_", identifier[..^1], identifier + "%", identifier.Replace('.', '_'),
                     unlistedSlug.ToUpperInvariant(), identifier.ToLowerInvariant(),
                     "rockstar", "Rockstar Stylist", title, "NicolasDorier", ServerTester.RepoUrl,
                     hiddenSlug, hiddenIdentifier
                 })
        {
            var html = await Search(search);
            AssertPlugin(html, unlistedSlug, search, present: false);
            AssertPlugin(html, lookalikeSlug, search, present: false);
            AssertPlugin(html, hiddenSlug, search, present: false);
        }

        foreach (var search in new[] { "", "rockstar", "Rockstar Stylist", "Shared", title, "NicolasDorier", ServerTester.RepoUrl })
            AssertPlugin(await Search(search), listedSlug, search, present: true);

        AssertPlugin(await Search(lookalikeSlug), lookalikeSlug, lookalikeSlug, present: true);
        AssertPlugin(await Search(unlistedSlug, "&btcpayVersion=0.0.1"), unlistedSlug, unlistedSlug, present: false);

        Task<string> Search(string search, string extraQuery = "") => client.GetStringAsync(
            $"/public/plugins?searchPluginName={Uri.EscapeDataString(search)}&embed={(embedded ? "1" : "0")}{extraQuery}");
    }

    private static void AssertPlugin(string html, string slug, string search, bool present) =>
        Assert.True(html.Contains($"data-plugin-slug=\"{slug}\"", StringComparison.Ordinal) == present,
            $"Expected plugin '{slug}' to be {(present ? "present" : "absent")} for search '{search}'.");
}
