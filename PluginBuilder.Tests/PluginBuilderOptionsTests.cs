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

    [Theory]
    [InlineData("token")]
    [InlineData("./token")]
    [InlineData("../token")]
    [InlineData("/token\nvalue")]
    public void ProductionRejectsRelativeOrControlCharacterBrokerTokenPath(string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BUILD_BROKER_TOKEN_FILE"] = value
        }).Build();

        var error = Assert.Throws<ConfigurationException>(() =>
            PluginBuilderOptions.ConfigureDataDirAndDebugLog(
                configuration, new TestHostEnvironment(Environments.Production, "/app")));

        Assert.Equal("BUILD_BROKER_TOKEN_FILE", error.Key);
    }

    [Fact]
    public void ConfigurationDefaultsToBrokerAndDoesNotInventASecret()
    {
        var options = PluginBuilderOptions.ConfigureDataDirAndDebugLog(new ConfigurationBuilder().Build(), null!);
        Assert.Equal("http://build-broker:8080/", options.BuildBrokerUrl.AbsoluteUri);
        Assert.Null(options.BuildBrokerTokenFile);
    }

    [Theory]
    [InlineData("unix:///var/run/docker.sock")]
    [InlineData("file:///tmp/broker")]
    [InlineData("http://user:password@build-broker:8080")]
    [InlineData("http://build-broker:8080/other")]
    [InlineData("http://build-broker:8080/?token=secret")]
    [InlineData("http://build-broker:8080/#fragment")]
    [InlineData("build-broker:8080")]
    [InlineData("http://build-broker:8080/\n")]
    public void BrokerEndpointIsAFixedOriginWithoutCredentialsOrPaths(string value)
    {
        Assert.Equal("BUILD_BROKER_URL", Assert.Throws<ConfigurationException>(
            () => PluginBuilderOptions.ParseBuildBrokerUrl(value)).Key);
    }

    [Theory]
    [InlineData("http://build-broker:8080", "http://build-broker:8080/")]
    [InlineData("https://build-broker", "https://build-broker/")]
    public void TrustedOperatorCanConfigureHttpOrHttpsBrokerOrigin(string value, string expected)
    {
        Assert.Equal(expected, PluginBuilderOptions.ParseBuildBrokerUrl(value).AbsoluteUri);
    }

    private sealed class TestHostEnvironment(string environmentName, string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "PluginBuilder.Tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
