using PluginBuilder.DataModels;

namespace PluginBuilder.ViewModels.Admin;

public class PluginViewModel
{
    public string Slug { get; set; } = null!;
    public string Identifier { get; set; } = null!;
    public string Settings { get; set; } = null!;
    public PluginVisibilityEnum Visibility { get; set; }
}
