namespace PluginBuilder.DataModels;

public class PluginDownload
{
    public long Id { get; set; }
    public string PluginSlug { get; set; } = null!;
    public string Version { get; set; } = null!;
    public DateTimeOffset Timestamp { get; set; }
    public string HashedIp { get; set; } = null!;
    public string BTCPayVersion { get; set; } = null!;
}

public class PluginServerInstall
{
    public string HashedIp { get; set; } = null!;
    public string PluginSlug { get; set; } = null!;
    public string Version { get; set; } = null!;
    public string BTCPayVersion { get; set; } = null!;
    public long InstallCount { get; set; }
    public DateTimeOffset InstalledAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? UninstalledAt { get; set; }
}
