using System.Text;
using Xunit;

namespace PluginBuilder.Tests;

public class ServerTesterDatabaseNameTests
{
    [Fact]
    public void FreshDatabaseKeepsShortReadablePrefix()
    {
        var name = ServerTester.CreateDatabaseName("CanPackPlugin", reuseDatabase: false);

        Assert.StartsWith("canpackplugin_", name, StringComparison.Ordinal);
        Assert.True(Guid.TryParseExact(name["canpackplugin_".Length..], "N", out _));
    }

    [Theory]
    [InlineData("DownloadEndpoint_UsesInternalLoopbackRedirectWhenLocalArtifactProxyEnabled")]
    [InlineData("DownloadEndpoint_DoesNotUseInternalLoopbackRedirectWhenLocalArtifactProxyDisabled")]
    public void LongTestNamesPreserveUniqueSuffixWithinPostgresLimit(string testFolder)
    {
        var first = ServerTester.CreateDatabaseName(testFolder, reuseDatabase: false);
        var second = ServerTester.CreateDatabaseName(testFolder, reuseDatabase: false);

        Assert.Equal(63, Encoding.UTF8.GetByteCount(first));
        Assert.Equal(63, Encoding.UTF8.GetByteCount(second));
        Assert.NotEqual(first, second);
        Assert.StartsWith(testFolder[..30].ToLowerInvariant() + "_", first, StringComparison.Ordinal);
        Assert.True(Guid.TryParseExact(first[^32..], "N", out _));
        Assert.True(Guid.TryParseExact(second[^32..], "N", out _));
    }

    [Theory]
    [InlineData("Integração_日本語/Build #1")]
    [InlineData("日本語日本語日本語日本語日本語日本語日本語日本語日本語日本語日本語日本語")]
    [InlineData("1234567890123456789012345678901234567890")]
    [InlineData("")]
    public void FreshDatabaseUsesBoundedAsciiIdentifier(string testFolder)
    {
        var name = ServerTester.CreateDatabaseName(testFolder, reuseDatabase: false);

        Assert.Matches("^[a-z_][a-z0-9_]*_[0-9a-f]{32}$", name);
        Assert.Equal(name.Length, Encoding.UTF8.GetByteCount(name));
        Assert.InRange(name.Length, 34, 63);
        Assert.True(Guid.TryParseExact(name[^32..], "N", out _));
    }

    [Theory]
    [InlineData("CanPackPlugin")]
    [InlineData("DownloadEndpoint_UsesInternalLoopbackRedirectWhenLocalArtifactProxyEnabled")]
    [InlineData("Integração_日本語/Build #1")]
    public void ReusedDatabaseKeepsExistingNamingBehavior(string testFolder)
    {
        var first = ServerTester.CreateDatabaseName(testFolder, reuseDatabase: true);
        var second = ServerTester.CreateDatabaseName(testFolder, reuseDatabase: true);

        Assert.Equal(testFolder.ToLowerInvariant(), first);
        Assert.Equal(first, second);
    }
}
