using System.Net;
using Dapper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PluginBuilder.APIModels;
using PluginBuilder.DataModels;
using PluginBuilder.Services;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.ApiTests;

public class PluginBySlugApiTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    [Fact]
    public async Task ReturnsListedAndUnlistedPluginsAsSingleObjectButHidesHiddenPlugins()
    {
        await using var tester = Create();
        tester.ReuseDatabase = false;
        await tester.Start();

        var pluginSlug = UniqueSlug("slug-visibility");
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await InsertPlugin(conn, pluginSlug, PluginVisibilityEnum.Listed, new VersionFixture("1.0.0.0"));

        var client = tester.CreateHttpClient();
        using (var listedResponse = await client.GetAsync(DirectoryUrl(pluginSlug)))
            await AssertPublishedVersion(listedResponse, pluginSlug, "1.0.0.0");

        await SetVisibility(conn, pluginSlug, PluginVisibilityEnum.Unlisted);
        using (var unlistedResponse = await client.GetAsync(DirectoryUrl(pluginSlug)))
            await AssertPublishedVersion(unlistedResponse, pluginSlug, "1.0.0.0");

        await SetVisibility(conn, pluginSlug, PluginVisibilityEnum.Hidden);
        using var hiddenResponse = await client.GetAsync(DirectoryUrl(pluginSlug));
        Assert.Equal(HttpStatusCode.NotFound, hiddenResponse.StatusCode);
    }

    [Fact]
    public async Task RequiresAnExactValidSlug()
    {
        await using var tester = Create();
        tester.ReuseDatabase = false;
        await tester.Start();

        var pluginSlug = UniqueSlug("slug-exact");
        var unpublishedSlug = UniqueSlug("slug-unpublished");
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await InsertPlugin(conn, pluginSlug, PluginVisibilityEnum.Listed, new VersionFixture("1.0.0.0"));
        await InsertPlugin(conn, unpublishedSlug, PluginVisibilityEnum.Listed);

        var client = tester.CreateHttpClient();

        using var partialResponse = await client.GetAsync(DirectoryUrl(pluginSlug[..^1]));
        Assert.Equal(HttpStatusCode.NotFound, partialResponse.StatusCode);

        using var unknownResponse = await client.GetAsync(DirectoryUrl("missing-plugin"));
        Assert.Equal(HttpStatusCode.NotFound, unknownResponse.StatusCode);

        using var unpublishedResponse = await client.GetAsync(DirectoryUrl(unpublishedSlug));
        Assert.Equal(HttpStatusCode.NotFound, unpublishedResponse.StatusCode);

        using var malformedResponse = await client.GetAsync(DirectoryUrl("bad-"));
        Assert.Equal(HttpStatusCode.BadRequest, malformedResponse.StatusCode);

        using var identifierResponse = await client.GetAsync(DirectoryUrl("[BTCPayServer.Plugins.Test]"));
        Assert.Equal(HttpStatusCode.BadRequest, identifierResponse.StatusCode);

        using var invalidBooleanResponse = await client.GetAsync(DirectoryUrl(pluginSlug, "?includePreRelease=invalid"));
        Assert.Equal(HttpStatusCode.BadRequest, invalidBooleanResponse.StatusCode);
    }

    [Fact]
    public async Task SelectsPrereleasesAndFiltersCompatibleVersions()
    {
        await using var tester = Create();
        tester.ReuseDatabase = false;
        await tester.Start();

        var pluginSlug = UniqueSlug("slug-versions");
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await InsertPlugin(
            conn,
            pluginSlug,
            PluginVisibilityEnum.Listed,
            new VersionFixture("1.0.0.0", BtcpayMinVersion: "2.3.0.0", BtcpayMaxVersion: "2.3.7.0"),
            new VersionFixture("2.0.0.0", PreRelease: true, BtcpayMinVersion: "2.4.0.0", BtcpayMaxVersion: "2.4.5.0"));
        var client = tester.CreateHttpClient();

        using var stableResponse = await client.GetAsync(DirectoryUrl(pluginSlug, "?btcpayVersion=2.3.7"));
        var stablePlugin = await AssertPublishedVersion(stableResponse, pluginSlug, "1.0.0.0");
        Assert.Equal("2.3.0.0", stablePlugin.BTCPayMinVersion);
        Assert.Equal("2.3.7.0", stablePlugin.BTCPayMaxVersion);

        using var prereleaseHostResponse = await client.GetAsync(DirectoryUrl(pluginSlug, "?btcpayVersion=2.3.7-rc2&includePreRelease=true"));
        var prereleaseHostPlugin = await AssertPublishedVersion(prereleaseHostResponse, pluginSlug, "1.0.0.0");
        Assert.Equal("2.3.0.0", prereleaseHostPlugin.BTCPayMinVersion);
        Assert.Equal("2.3.7.0", prereleaseHostPlugin.BTCPayMaxVersion);

        using var excludedCompatiblePrereleaseResponse = await client.GetAsync(DirectoryUrl(pluginSlug, "?btcpayVersion=2.4.0"));
        Assert.Equal(HttpStatusCode.NotFound, excludedCompatiblePrereleaseResponse.StatusCode);

        using var includedCompatiblePrereleaseResponse = await client.GetAsync(DirectoryUrl(pluginSlug, "?btcpayVersion=2.4.0&includePreRelease=true"));
        var compatiblePrereleasePlugin = await AssertPublishedVersion(includedCompatiblePrereleaseResponse, pluginSlug, "2.0.0.0");
        Assert.Equal("2.4.0.0", compatiblePrereleasePlugin.BTCPayMinVersion);
        Assert.Equal("2.4.5.0", compatiblePrereleasePlugin.BTCPayMaxVersion);

        using var incompatibleResponse = await client.GetAsync(DirectoryUrl(pluginSlug, "?btcpayVersion=2.4.5.1&includePreRelease=true"));
        Assert.Equal(HttpStatusCode.NotFound, incompatibleResponse.StatusCode);

        using var invalidHostResponse = await client.GetAsync(DirectoryUrl(pluginSlug, "?btcpayVersion=2.3.x-rc1"));
        Assert.Equal(HttpStatusCode.BadRequest, invalidHostResponse.StatusCode);
    }

    [Fact]
    public async Task CatalogUsesConfiguredTitlesAcrossVersionsAndForOrdering()
    {
        await using var tester = Create();
        tester.ReuseDatabase = false;
        await tester.Start();
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        var alpha = UniqueSlug("title-alpha");
        var zulu = UniqueSlug("title-zulu");
        await InsertPlugin(conn, alpha, PluginVisibilityEnum.Listed,
            new VersionFixture("1.0.0.0"), new VersionFixture("2.0.0.0", PreRelease: true));
        await InsertPlugin(conn, zulu, PluginVisibilityEnum.Listed, new VersionFixture("1.0.0.0"));
        await conn.ExecuteAsync("""
            UPDATE plugins SET settings=jsonb_build_object('pluginTitle',
                CASE WHEN slug=@alpha THEN 'Alpha configured' ELSE 'Zulu configured' END)
            WHERE slug IN (@alpha, @zulu);
            UPDATE builds SET manifest_info=jsonb_set(manifest_info, '{Name}', to_jsonb(
                CASE WHEN plugin_slug=@alpha AND id=1 THEN 'ZZZ package'
                     WHEN plugin_slug=@alpha THEN 'AAA other package' ELSE 'AAA package' END::text))
            WHERE plugin_slug IN (@alpha, @zulu);
            """, new { alpha, zulu });

        using var client = tester.CreateHttpClient();
        var versions = JArray.Parse(await client.GetStringAsync(
            "/api/v1/plugins?includeAllVersions=true&includePreRelease=true&btcpayVersion=2.3.7"))
            .ToObject<PublishedVersion[]>()!;
        Assert.Equal(new[] { "Alpha configured", "Alpha configured", "Zulu configured" },
            versions.Select(v => v.PluginTitle));
        var alphaVersions = versions.Where(v => v.ProjectSlug == alpha).ToArray();
        Assert.Equal(2, alphaVersions.Length);
        Assert.Equal(new[] { "AAA other package", "ZZZ package" },
            alphaVersions.Select(v => v.ManifestInfo["Name"]!.ToString()).Order());
        Assert.Empty(JArray.Parse(await client.GetStringAsync(
            "/api/v1/plugins?searchPluginName=ZZZ%20package&includePreRelease=true&includeAllVersions=true")));
    }

    private static async Task InsertPlugin(
        System.Data.IDbConnection conn,
        string pluginSlug,
        PluginVisibilityEnum visibility,
        params VersionFixture[] versions)
    {
        var identifier = IdentifierFor(pluginSlug);
        await conn.ExecuteAsync(
            """
            INSERT INTO plugins (slug, identifier, settings, visibility)
            VALUES (@pluginSlug, @identifier, @settings::JSONB, @visibility::plugin_visibility_enum)
            """,
            new
            {
                pluginSlug,
                identifier,
                settings = JsonConvert.SerializeObject(new { pluginTitle = pluginSlug, description = "Test plugin description" }),
                visibility = visibility.ToString().ToLowerInvariant()
            });

        for (var i = 0; i < versions.Length; i++)
        {
            var fixture = versions[i];
            var buildId = i + 1L;
            var manifestInfo = new JObject
            {
                ["Identifier"] = identifier,
                ["Name"] = "Test plugin",
                ["Version"] = fixture.Version,
                ["Description"] = "Test plugin description",
                ["Dependencies"] = new JArray()
            };

            await conn.ExecuteAsync(
                """
                INSERT INTO builds (plugin_slug, id, state, manifest_info, build_info)
                VALUES (@pluginSlug, @buildId, 'uploaded', @manifestInfo::JSONB, '{}'::JSONB);

                INSERT INTO versions (plugin_slug, ver, build_id, btcpay_min_ver, btcpay_max_ver, pre_release)
                VALUES (@pluginSlug, @version, @buildId, @btcpayMinVersion, @btcpayMaxVersion, @preRelease);
                """,
                new
                {
                    pluginSlug,
                    buildId,
                    manifestInfo = manifestInfo.ToString(Formatting.None),
                    version = PluginVersion.Parse(fixture.Version).VersionParts,
                    btcpayMinVersion = PluginVersion.Parse(fixture.BtcpayMinVersion).VersionParts,
                    btcpayMaxVersion = fixture.BtcpayMaxVersion is null
                        ? null
                        : PluginVersion.Parse(fixture.BtcpayMaxVersion).VersionParts,
                    preRelease = fixture.PreRelease
                });
        }
    }

    private static Task SetVisibility(System.Data.IDbConnection conn, string pluginSlug, PluginVisibilityEnum visibility)
    {
        return conn.ExecuteAsync(
            "UPDATE plugins SET visibility = @visibility::plugin_visibility_enum WHERE slug = @pluginSlug",
            new { pluginSlug, visibility = visibility.ToString().ToLowerInvariant() });
    }

    private static string DirectoryUrl(string pluginSlug, string query = "")
    {
        return $"/api/v1/plugins/directory/{Uri.EscapeDataString(pluginSlug)}{query}";
    }

    private static async Task<PublishedVersion> AssertPublishedVersion(
        HttpResponseMessage response,
        string expectedSlug,
        string expectedVersion)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JToken.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JTokenType.Object, json.Type);
        var publishedVersion = json.ToObject<PublishedVersion>() ?? throw new InvalidOperationException("Expected a published plugin version.");
        Assert.Equal(expectedSlug, publishedVersion.ProjectSlug);
        Assert.Equal(expectedSlug, publishedVersion.PluginTitle);
        Assert.Equal(expectedVersion, publishedVersion.Version);
        Assert.Equal(IdentifierFor(expectedSlug), publishedVersion.ManifestInfo?["Identifier"]?.ToString());
        Assert.NotNull(publishedVersion.BuildInfo);
        return publishedVersion;
    }

    private static string IdentifierFor(string pluginSlug) => $"BTCPayServer.Plugins.{pluginSlug.Replace('-', '.')}";

    private static string UniqueSlug(string prefix)
    {
        return $"{prefix}-{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 9, 30)];
    }

    private sealed record VersionFixture(
        string Version,
        bool PreRelease = false,
        string BtcpayMinVersion = "0.0.0.0",
        string? BtcpayMaxVersion = null);
}
