using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using PluginBuilder.Configuration;
using PluginBuilder.Util.Extensions;
using Serilog.Events;
using Xunit;

namespace PluginBuilder.Tests;

public class PluginBuilderOptionsTests
{
    [Fact]
    public void RetiredExecutorSettingsDoNotBlockPublicConfiguration()
    {
        var tokenPath = Path.Combine(Path.GetTempPath(), "plugin-builder-test-broker-token");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BUILD_TIMEOUT_SECONDS"] = "invalid-retired-setting",
            ["BUILD_SCRATCH_ROOT"] = "../old-scratch",
            ["BUILD_BROKER_URL"] = "http://internal-broker:8080",
            ["BUILD_BROKER_TOKEN_FILE"] = tokenPath,
            ["debugloglevel"] = "Warning",
            ["debuglogretaincount"] = "3"
        }).Build();

        var options = PluginBuilderOptions.ConfigureDataDirAndDebugLog(configuration, null!);

        Assert.Equal(new Uri("http://internal-broker:8080/"), options.BuildBrokerUrl);
        Assert.Equal(tokenPath, options.BuildBrokerTokenFile);
        Assert.Equal(LogEventLevel.Warning, options.DebugLogLevel);
        Assert.Equal(3, options.LogRetainCount);
        Assert.Equal(Path.Combine(options.DataDir, "PluginData"), options.PluginDataDir);
    }

    [Fact]
    public void DevelopmentResolvesRelativeBrokerTokenAgainstContentRoot()
    {
        const string relativePath = "../PluginBuilder.Tests/dev-broker-token";
        var contentRoot = Path.Combine(Path.GetTempPath(), "plugin-builder-project");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BUILD_BROKER_TOKEN_FILE"] = relativePath
        }).Build();

        var options = PluginBuilderOptions.ConfigureDataDirAndDebugLog(
            configuration, new TestHostEnvironment(Environments.Development, contentRoot));

        Assert.Equal(Path.GetFullPath(Path.Combine(contentRoot, relativePath)), options.BuildBrokerTokenFile);
    }

    [Fact]
    public void ProductionRejectsRelativeBrokerTokenPath()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BUILD_BROKER_TOKEN_FILE"] = "../token"
        }).Build();

        var error = Assert.Throws<ConfigurationException>(() =>
            PluginBuilderOptions.ConfigureDataDirAndDebugLog(
                configuration, new TestHostEnvironment(Environments.Production, "/app")));

        Assert.Equal("BUILD_BROKER_TOKEN_FILE", error.Key);
    }

    private sealed class TestHostEnvironment(string environmentName, string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "PluginBuilder.Tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
