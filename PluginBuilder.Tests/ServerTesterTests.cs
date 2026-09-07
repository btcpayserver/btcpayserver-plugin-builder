using Xunit;

namespace PluginBuilder.Tests;

public class ServerTesterTests
{
    [Fact]
    public void FreshBuildsHaveDistinctArtifactNamespaces()
    {
        var first = new FullBuildId(ServerTester.CreatePluginSlug(), 0);
        var second = new FullBuildId(ServerTester.CreatePluginSlug(), 0);

        Assert.True(PluginSlug.IsValidSlugName(first.PluginSlug.ToString()));
        Assert.True(PluginSlug.IsValidSlugName(second.PluginSlug.ToString()));
        Assert.StartsWith("rockstar-", first.PluginSlug.ToString());
        Assert.StartsWith("rockstar-", second.PluginSlug.ToString());
        Assert.NotEqual(first.ToString(), second.ToString());
    }
}
