using System.Security;
using System.Threading.Tasks;
using Dapper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PluginBuilder.Extensions;
using PluginBuilder.Services;
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

        var buildService = tester.GetService<BuildService>();
        using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.NewPlugin("rockstar-stylist");
        var build = await conn.NewBuild("rockstar-stylist", new PluginBuildParameters("https://github.com/NicolasDorier/btcpayserver")
        {
            PluginDirectory = "Plugins/BTCPayServer.Plugins.RockstarStylist",
            GitRef = "plugins/collection2"
        });
        //https://github.com/NicolasDorier/btcpayserver/tree/plugins/collection2/Plugins/BTCPayServer.Plugins.RockstarStylist
        var fullBuildId = new FullBuildId("rockstar-stylist", build);
        await buildService.Build(fullBuildId);

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
        await conn.NewPlugin("rockstar-stylist-fake");
        build = await conn.NewBuild("rockstar-stylist-fake", new PluginBuildParameters("https://github.com/NicolasDorier/btcpayserver")
        {
            PluginDirectory = "Plugins/BTCPayServer.Plugins.RockstarStylist",
            GitRef = "plugins/collection2"
        });
        fullBuildId = new FullBuildId("rockstar-stylist-fake", build);
        await buildService.Build(fullBuildId);

        var rockstarPlugins = await conn.QueryAsync<string?>("SELECT slug FROM plugins WHERE identifier='BTCPayServer.Plugins.RockstarStylist'");
        var p = Assert.Single(rockstarPlugins);
        Assert.Equal("rockstar-stylist", p);
        versions = await client.GetPublishedVersions("1.4.6.0", true);
        version = Assert.Single(versions);
        Assert.Equal("rockstar-stylist", version.ProjectSlug);

        // Let's see what happen if there is two versions of the same plugin
        await conn.ExecuteAsync("INSERT INTO versions VALUES('rockstar-stylist', ARRAY[1,0,2,1], 0, ARRAY[1,4,6,0], 'f', CURRENT_TIMESTAMP)");
        versions = await client.GetPublishedVersions("1.4.6.0", true);
        version = Assert.Single(versions);
        Assert.Equal("1.0.2.1", version.Version);
        versions = await client.GetPublishedVersions("1.4.6.0", true, includeAllVersions: true);
        Assert.Equal("1.0.2.1", versions[0].Version);
        Assert.Equal("1.0.2.0", versions[1].Version);
    }
}
