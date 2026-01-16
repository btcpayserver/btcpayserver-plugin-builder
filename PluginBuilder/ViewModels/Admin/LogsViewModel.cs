namespace PluginBuilder.ViewModels.Admin;

public class LogsViewModel
{
    public List<FileInfo> LogFiles { get; set; } = new();
    public string? Log { get; set; }
    public int LogFileCount { get; set; }
    public int LogFileOffset { get; set; }
}
