using Serilog.Events;

namespace PluginBuilder.Configuration;

public sealed class PluginBuilderOptions
{
    public required string DataDir { get; init; }
    public required string LogFile { get; init; }
    public required LogEventLevel LogLevel { get; init; }

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
            Environment.GetEnvironmentVariable("PLUGIN_BUILDER_LOG_FILE") ??
            "logs/pluginbuilder.log";
        var logFile = Path.IsPathRooted(rawLog) ? rawLog : Path.GetFullPath(Path.Combine(dataDir, rawLog));
        Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);

        var rawLevel = conf["debugloglevel"] ??
                       (env.IsDevelopment() ? nameof(LogEventLevel.Debug) : nameof(LogEventLevel.Information));
        var level = Enum.TryParse(rawLevel, true, out LogEventLevel parsed) ? parsed : LogEventLevel.Information;

        return new PluginBuilderOptions
        {
            DataDir = dataDir,
            LogFile = logFile,
            LogLevel = level
        };
    }
}
