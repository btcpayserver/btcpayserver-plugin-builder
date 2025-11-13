using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.OutputCaching;
using Newtonsoft.Json;
using PluginBuilder.APIModels;
using PluginBuilder.DataModels;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests;

public class UnitTest1 : UnitTestBase
{
    public UnitTest1(ITestOutputHelper logs) : base(logs)
    {
    }

    [Fact]
    public async Task Test1()
    {
        await using var tester = await Start();
    }

    [Theory]
    [InlineData("test-6", true)]
    [InlineData("test-6-", false)]
    [InlineData("6test-6", false)]
    [InlineData("-test-6", false)]
    [InlineData("te", false)]
    [InlineData("teqoeteqoeteqoeteqoeteqoeteqoee", false)]
    [InlineData("teqoeteqoeteqoeteqoeteqoet", true)]
    public void IsValidSlugTest(string slug, bool expected)
    {
        Assert.Equal(expected, PluginSlug.IsValidSlugName(slug));
    }

    [Fact]
    public async Task CanPackPlugin()
    {
        await using var tester = Create();
        tester.ReuseDatabase = false;
        await tester.Start();

        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        //https://github.com/NicolasDorier/btcpayserver/tree/plugins/collection2/Plugins/BTCPayServer.Plugins.RockstarStylist
        var ownerId = await tester.CreateFakeUserAsync();
        var fullBuildId = await tester.CreateAndBuildPluginAsync(ownerId);

        var client = tester.CreateHttpClient();
        var versions = await client.GetPublishedVersions("1.4.6.0", true);
        var version = Assert.Single(versions);
        Assert.NotNull(version);
        var prev = version;
        version = await client.GetPlugin(version.ProjectSlug, version.Version);
        Assert.NotNull(version);
        Assert.Equal(JsonConvert.SerializeObject(version), JsonConvert.SerializeObject(prev));

        Assert.Null(await client.GetPlugin(version.ProjectSlug, "10.0.0.1"));
        Assert.Equal("1.0.2.0", version.Version);
        versions = await client.GetPublishedVersions("1.4.5.9", true);
        Assert.Empty(versions);
        versions = await client.GetPublishedVersions("1.4.6.0", false);
        Assert.Empty(versions);

        // Can download the project?
        var b1 = await client.DownloadPlugin(new PluginSelectorBySlug("rockstar-stylist"), PluginVersion.Parse("1.0.2.0"));
        var b2 = await client.DownloadPlugin(new PluginSelectorByIdentifier("BTCPayServer.Plugins.RockstarStylist"), PluginVersion.Parse("1.0.2.0"));
        Assert.NotNull(b1);
        Assert.NotNull(b2);
        Assert.Equal(b1.Length, b2.Length);


        var manifest = PluginManifest.Parse(version.ManifestInfo.ToString());

        // Nothing changed
        Assert.False(await conn.SetVersionBuild(fullBuildId, manifest.Version, manifest.BTCPayMinVersion, true));
        // Can change BTCPayMinVersion
        Assert.True(await conn.SetVersionBuild(fullBuildId, manifest.Version, null, true));
        // Can remove pre-release
        Assert.True(await conn.SetVersionBuild(fullBuildId, manifest.Version, manifest.BTCPayMinVersion, false));

        // Can't put back in pre-release
        Assert.False(await conn.SetVersionBuild(fullBuildId, manifest.Version, manifest.BTCPayMinVersion, true));
        // Can't modify pre-release
        Assert.False(await conn.SetVersionBuild(fullBuildId, manifest.Version, null, false));


        // Another plugin slug try to hijack the package
        await tester.CreateAndBuildPluginAsync(
            ownerId,
            slug: "rockstar-stylist-fake",
            gitRef: "plugins/collection2",
            pluginDir: "Plugins/BTCPayServer.Plugins.RockstarStylist"
        );

        var rockstarPlugins =
            await conn.QueryAsync<string?>("SELECT slug FROM plugins WHERE identifier='BTCPayServer.Plugins.RockstarStylist'");
        var p = Assert.Single(rockstarPlugins);
        Assert.Equal("rockstar-stylist", p);
        versions = await client.GetPublishedVersions("1.4.6.0", true);
        version = Assert.Single(versions);
        Assert.Equal("rockstar-stylist", version.ProjectSlug);

        // Let's see what happen if there is two versions of the same plugin
        await conn.ExecuteAsync("INSERT INTO versions VALUES('rockstar-stylist', ARRAY[1,0,2,1], 0, ARRAY[1,4,6,0], 'f', CURRENT_TIMESTAMP, NULL)");
        var outputCacheStore = tester.GetService<IOutputCacheStore>();
        await outputCacheStore.EvictByTagAsync(CacheTags.Plugins, CancellationToken.None);
        versions = await client.GetPublishedVersions("1.4.6.0", true);
        version = Assert.Single(versions);
        Assert.Equal("1.0.2.1", version.Version);
        versions = await client.GetPublishedVersions("1.4.6.0", true, true);
        Assert.Equal("1.0.2.1", versions[1].Version);
        Assert.Equal("1.0.2.0", versions[0].Version);

        // listed - always render
        await conn.ExecuteAsync("UPDATE plugins SET visibility = 'listed' WHERE slug = 'rockstar-stylist'");
        await outputCacheStore.EvictByTagAsync(CacheTags.Plugins, CancellationToken.None);
        var res = await client.GetPublishedVersions("2.1.0.0", false);
        Assert.Contains(res, p => p.ProjectSlug == "rockstar-stylist");

        // unlisted - only render with compatible search term or legacy versions
        await conn.ExecuteAsync("UPDATE plugins SET visibility = 'unlisted' WHERE slug = 'rockstar-stylist'");
        await outputCacheStore.EvictByTagAsync(CacheTags.Plugins, CancellationToken.None);
        res = await client.GetPublishedVersions("2.1.0.0", false);
        Assert.DoesNotContain(res, p => p.ProjectSlug == "rockstar-stylist");

        res = await client.GetPublishedVersions("2.1.0.0", false, searchPluginName: "rockstar");
        Assert.Contains(res, p => p.ProjectSlug == "rockstar-stylist");

        var raw = await client.GetStringAsync("/api/v1/plugins");
        var legacyRes = JsonConvert.DeserializeObject<PublishedVersion[]>(raw);
        Assert.Contains(legacyRes, p => p.ProjectSlug == "rockstar-stylist");

        // hidden - never render
        await conn.ExecuteAsync("UPDATE plugins SET visibility = 'hidden' WHERE slug = 'rockstar-stylist'");
        await outputCacheStore.EvictByTagAsync(CacheTags.Plugins, CancellationToken.None);
        res = await client.GetPublishedVersions("2.1.0.0", false);
        Assert.DoesNotContain(res, p => p.ProjectSlug == "rockstar-stylist");

        res = await client.GetPublishedVersions("2.1.0.0", false, searchPluginName: "rockstar");
        Assert.DoesNotContain(res, p => p.ProjectSlug == "rockstar-stylist");
    }
}
