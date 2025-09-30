using Serilog.Events;

namespace PluginBuilder.Configuration;

public sealed class PluginBuilderOptions
{
    public required string DataDir { get; init; }
    public string? DebugLogFile { get; init; }
    public LogEventLevel? DebugLogLevel { get; init; }
    public int LogRetainCount { get; init; } = 1;

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
            {
                Directory.CreateDirectory(logDir);
            }
        }

        var rawLevel = conf["debugloglevel"];
        LogEventLevel? level = null;
        if (!string.IsNullOrWhiteSpace(rawLevel) &&
            Enum.TryParse(rawLevel, true, out LogEventLevel parsed))
        {
            level = parsed;
        }

        var retainRaw = conf["debuglogretaincount"];
        var retain = 1;
        if (int.TryParse(retainRaw, out var retainParsed) && retainParsed > 0)
            retain = retainParsed;


        return new PluginBuilderOptions
        {
            DataDir = dataDir,
            DebugLogFile = logFile,
            DebugLogLevel = level,
            LogRetainCount = retain
        };
    }
}
