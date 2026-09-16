using Serilog.Events;
using PluginBuilder.Util.Extensions;

namespace PluginBuilder.Configuration;

public sealed class PluginBuilderOptions
{
    public required string DataDir { get; init; }
    public Uri BuildBrokerUrl { get; init; } = new("http://build-broker:8080/");
    public string? BuildBrokerTokenFile { get; init; }
    public string? DebugLogFile { get; init; }
    public LogEventLevel? DebugLogLevel { get; init; }
    public int LogRetainCount { get; init; } = 1;
    public string PluginDataDir => Path.Combine(DataDir, "PluginData");
    public static PluginBuilderOptions ConfigureDataDirAndDebugLog(IConfiguration conf, IHostEnvironment env)
    {
        var dataDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BTCPayServer-PluginBuilder");
        Directory.CreateDirectory(dataDir);

        var rawLog = conf["debuglog"];

        string? logFile = null;
        if (!string.IsNullOrWhiteSpace(rawLog))
        {
            logFile = Path.IsPathRooted(rawLog)
                ? rawLog
                : Path.GetFullPath(Path.Combine(dataDir, rawLog));

            var logDir = Path.GetDirectoryName(logFile);

            if (!string.IsNullOrEmpty(logDir))
                Directory.CreateDirectory(logDir);
        }

        var rawLevel = conf["debugloglevel"];
        LogEventLevel? level = null;
        if (!string.IsNullOrWhiteSpace(rawLevel) &&
            Enum.TryParse(rawLevel, true, out LogEventLevel parsed))
            level = parsed;

        var retainRaw = conf["debuglogretaincount"];
        var retain = 1;
        if (int.TryParse(retainRaw, out var retainParsed) && retainParsed > 0)
            retain = retainParsed;

        var brokerUrl = ParseBuildBrokerUrl(conf["BUILD_BROKER_URL"] ?? "http://build-broker:8080/");
        var brokerTokenFile = conf["BUILD_BROKER_TOKEN_FILE"]?.Trim();
        if (string.IsNullOrEmpty(brokerTokenFile))
            brokerTokenFile = null;
        else if (brokerTokenFile.Any(char.IsControl) || !Path.IsPathFullyQualified(brokerTokenFile))
            throw new ConfigurationException("BUILD_BROKER_TOKEN_FILE", "Must be an absolute secret-file path");

        return new PluginBuilderOptions
        {
            DataDir = dataDir,
            BuildBrokerUrl = brokerUrl,
            BuildBrokerTokenFile = brokerTokenFile,
            DebugLogFile = logFile,
            DebugLogLevel = level,
            LogRetainCount = retain
        };
    }

    public static Uri ParseBuildBrokerUrl(string value)
    {
        if (value.Any(char.IsControl) || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ConfigurationException("BUILD_BROKER_URL", "Must be a fixed HTTP(S) origin without credentials, path, query, or fragment");
        return uri;
    }
}
