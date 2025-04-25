#nullable disable


using PluginBuilder.Components.PluginVersion;

namespace PluginBuilder.Components.MainNav;

public class MainNavViewModel
{
    public string PluginSlug { get; set; }

    public List<PluginVersionViewModel> Versions { get; set; } = new();
}
