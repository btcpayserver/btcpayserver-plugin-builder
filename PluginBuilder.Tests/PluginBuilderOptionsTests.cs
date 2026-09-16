using Microsoft.Extensions.Configuration;
using PluginBuilder.Configuration;
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
}
