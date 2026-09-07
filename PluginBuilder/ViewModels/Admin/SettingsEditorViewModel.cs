namespace PluginBuilder.ViewModels.Admin;

public class SettingsEditorViewModel
{
    public List<(string key, string value)> Settings { get; init; } = [];
    public string WhitelistVersion { get; init; } = string.Empty;
}
