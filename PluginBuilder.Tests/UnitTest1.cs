using System.Threading.Tasks;
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

        //https://github.com/Kukks/btcpayserver/tree/plugins/collection/Plugins/BTCPayServer.Plugins.RockstarStylist
        await buildService.Build("rockstar-stylist",
        new PluginBuildParameters("https://github.com/Kukks/btcpayserver")
        {
            PluginDirectory = "Plugins/BTCPayServer.Plugins.RockstarStylist",
            GitRef = "plugins/collection"
        });
    }
}
