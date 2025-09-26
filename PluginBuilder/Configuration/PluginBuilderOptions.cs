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
            conf["DATADIR"] ??
            conf["datadir"] ??
            conf["PluginBuilder:DataDir"] ??
            Path.Combine(env.ContentRootPath, "Data");
        Directory.CreateDirectory(dataDir);

        var rawLog =
            conf["debuglog"] ??
            conf["PluginBuilder:LogFile"] ??
            Environment.GetEnvironmentVariable("PLUGIN_BUILDER_LOG_FILE");

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

        var rawLevel = conf["debugloglevel"] ?? conf["PluginBuilder:DebugLogLevel"];
        LogEventLevel? level = null;
        if (!string.IsNullOrWhiteSpace(rawLevel) &&
            Enum.TryParse(rawLevel, true, out LogEventLevel parsed))
        {
            level = parsed;
        }

        var retainRaw = conf["PluginBuilder:LogRetainCount"] ?? conf["debuglogretaincount"];
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
