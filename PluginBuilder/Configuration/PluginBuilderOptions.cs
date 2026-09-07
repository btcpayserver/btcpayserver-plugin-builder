using System.Globalization;
using Serilog.Events;
using PluginBuilder.Util.Extensions;

namespace PluginBuilder.Configuration;

public sealed class PluginBuilderOptions
{
    private const int DefaultBuildTimeoutSeconds = 15 * 60;
    private const int MaxBuildTimeoutSeconds = 24 * 60 * 60;

    public required string DataDir { get; init; }
    public TimeSpan BuildTimeout { get; init; } = TimeSpan.FromSeconds(DefaultBuildTimeoutSeconds);
    public string? BuildScratchRoot { get; init; }
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

        var buildTimeoutSeconds = DefaultBuildTimeoutSeconds;
        var buildTimeoutRaw = conf["BUILD_TIMEOUT_SECONDS"];
        if (!string.IsNullOrWhiteSpace(buildTimeoutRaw) &&
            (!int.TryParse(buildTimeoutRaw, NumberStyles.None, CultureInfo.InvariantCulture, out buildTimeoutSeconds) ||
             buildTimeoutSeconds is <= 0 or > MaxBuildTimeoutSeconds))
            throw new ConfigurationException("BUILD_TIMEOUT_SECONDS",
                $"Must be a positive integer no greater than {MaxBuildTimeoutSeconds}");

        var buildScratchRoot = conf["BUILD_SCRATCH_ROOT"]?.Trim();
        if (string.IsNullOrEmpty(buildScratchRoot))
            buildScratchRoot = null;

        return new PluginBuilderOptions
        {
            DataDir = dataDir,
            BuildTimeout = TimeSpan.FromSeconds(buildTimeoutSeconds),
            BuildScratchRoot = buildScratchRoot,
            DebugLogFile = logFile,
            DebugLogLevel = level,
            LogRetainCount = retain
        };
    }
}
