using Microsoft.Extensions.Configuration;
using PluginBuilder.BuildBroker.Configuration;
using Xunit;

namespace PluginBuilder.Tests;

public class BuildExecutorOptionsTests
{
    [Theory]
    [InlineData(false, null, "runsc")]
    [InlineData(true, null, "runsc")]
    [InlineData(false, "false", "runsc")]
    [InlineData(true, "false", "runsc")]
    [InlineData(true, "true", "runc")]
    public void RuntimeRequiresExplicitDevelopmentOptIn(bool development, string? useRunc, string runtime)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["USE_RUNC"] = useRunc
        }).Build();

        Assert.Equal(runtime, BuildExecutorOptions.FromConfiguration(config, development).Runtime);
    }

    [Theory]
    [InlineData("USE_RUNC", "true")]
    [InlineData("BUILD_SCRATCH_HOST_ROOT", "/docker/scratch")]
    public void ProductionRejectsDevelopmentSettings(string key, string value)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [key] = value
        }).Build();

        Assert.Throws<InvalidOperationException>(() => BuildExecutorOptions.FromConfiguration(config, false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData("/scratch,target=/")]
    [InlineData("/scratch\n")]
    public void DevelopmentRejectsInvalidDockerHostPath(string hostRoot)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BUILD_SCRATCH_HOST_ROOT"] = hostRoot
        }).Build();

        Assert.Throws<InvalidOperationException>(() => BuildExecutorOptions.FromConfiguration(config, true));
    }

    [Fact]
    public void DockerMountPathsMapToHostVolumeWithoutChangingBrokerPaths()
    {
        var brokerRoot = Path.Combine(Path.GetTempPath(), "broker-scratch");
        var hostRoot = Path.Combine(Path.GetTempPath(), "docker-volume");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BUILD_SCRATCH_ROOT"] = brokerRoot,
            ["BUILD_SCRATCH_HOST_ROOT"] = hostRoot
        }).Build();
        var options = BuildExecutorOptions.FromConfiguration(config, true);
        var source = Path.Combine(brokerRoot, "pb-build-example", "source");

        Assert.Equal(brokerRoot, options.BuildScratchRoot);
        Assert.Equal(hostRoot, options.DockerPath(brokerRoot));
        Assert.Equal(Path.Combine(hostRoot, "pb-build-example", "source"), options.DockerPath(source));
        Assert.Equal(source, new BuildExecutorOptions().DockerPath(source));
        Assert.Throws<InvalidOperationException>(() => options.DockerPath(brokerRoot + "-other"));
        Assert.Throws<InvalidOperationException>(() => options.DockerPath(Path.Combine(brokerRoot, "..", "outside")));
    }
}
