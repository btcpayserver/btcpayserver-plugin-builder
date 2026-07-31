using PluginBuilder.Services;
using Xunit;

namespace PluginBuilder.Tests;

public class PluginProjectMetadataTests
{
    [Fact]
    public void ParsesIdentifierAndCustomBuildImage()
    {
        const string buildImage =
            "docker.io/boltz/btcpay-arkade-builder@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var metadata = PluginProjectMetadata.Parse(
            $"""
            <Project>
              <PropertyGroup>
                <AssemblyName>Boltz.Arkade</AssemblyName>
                <PluginBuildImage>
                  {buildImage}
                </PluginBuildImage>
              </PropertyGroup>
            </Project>
            """,
            "Arkade.csproj");

        Assert.Equal("Boltz.Arkade", metadata.Identifier);
        Assert.Equal(buildImage, metadata.BuildImage);
    }

    [Fact]
    public void RejectsMutableCustomBuildImage()
    {
        var exception = Assert.Throws<BuildServiceException>(() => PluginProjectMetadata.Parse(
            "<Project><PropertyGroup><PluginBuildImage>docker.io/example/plugin-builder:v1</PluginBuildImage></PropertyGroup></Project>",
            "Example.csproj"));

        Assert.Contains("must be an immutable repository@sha256 digest", exception.Message);
    }
}
