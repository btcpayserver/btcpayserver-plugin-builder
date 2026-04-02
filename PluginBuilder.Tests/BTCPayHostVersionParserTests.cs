using PluginBuilder.ModelBinders;
using Xunit;

namespace PluginBuilder.Tests;

public class BtcPayHostVersionParserTests
{
    [Theory]
    [InlineData("2.3.7-rc2", "2.3.7")]
    [InlineData("  v2.3.7-rc2+sha  ", "2.3.7")]
    [InlineData("2.3.7.0-rc1", "2.3.7.0")]
    public void TryParse_AcceptsSupportedHostVersions(string input, string expected)
    {
        Assert.True(BtcPayHostVersionParser.TryParse(input, out var version));
        Assert.NotNull(version);
        Assert.Equal(expected, version.ToString());
    }

    [Theory]
    [InlineData("2.3.x-rc1")]
    [InlineData("2..3")]
    [InlineData("2.-3.0")]
    [InlineData("1.2.3.4.5")]
    public void TryParse_RejectsMalformedHostVersions(string input)
    {
        Assert.False(BtcPayHostVersionParser.TryParse(input, out var version));
        Assert.Null(version);
    }
}
