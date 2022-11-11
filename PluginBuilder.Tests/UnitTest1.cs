using System.Threading.Tasks;
using Dapper;
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
        var build = await conn.NewBuild("rockstar-stylist", new PluginBuildParameters("https://github.com/Kukks/btcpayserver")
        {
            PluginDirectory = "Plugins/BTCPayServer.Plugins.RockstarStylist",
            GitRef = "plugins/collection"
        });
        //https://github.com/Kukks/btcpayserver/tree/plugins/collection/Plugins/BTCPayServer.Plugins.RockstarStylist
        var fullBuildId = new FullBuildId("rockstar-stylist", build);
        await buildService.Build(fullBuildId);

        var client = tester.CreateHttpClient();
        var versions = await client.GetPublishedVersions("1.4.6.0", true);
        var version = Assert.Single(versions);
        versions = await client.GetPublishedVersions("1.4.5.9", true);
        Assert.Empty(versions);
        versions = await client.GetPublishedVersions("1.4.6.0", false);
        Assert.Empty(versions);

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
    }
}
