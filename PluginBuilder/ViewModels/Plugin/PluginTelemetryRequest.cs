namespace PluginBuilder.ViewModels.Plugin;

public class PluginTelemetryRequest
{
    public List<PluginTelemetryItem> Plugins { get; set; } = new();
}

public class PluginTelemetryItem
{
    public string? Slug { get; set; }
    public string? Version { get; set; }
}
