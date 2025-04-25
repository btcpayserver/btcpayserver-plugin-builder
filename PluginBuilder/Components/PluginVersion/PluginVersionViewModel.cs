#nullable disable
namespace PluginBuilder.Components.PluginVersion;

public class PluginVersionViewModel
{
    public string Version { get; set; }
    public string PluginSlug { get; set; }
    public bool Published { get; set; }
    public bool PreRelease { get; set; }
    public bool Removed { get; set; }
    public bool HidePublishBadge { get; set; }

    public static PluginVersionViewModel CreateOrNull(string version, bool published, bool pre_release, string state, string pluginSlug)
    {
        if (version is null)
            return null;
        return new PluginVersionViewModel
        {
            Version = version,
            Published = published,
            PreRelease = pre_release,
            PluginSlug = pluginSlug,
            Removed = state == BuildStates.Removed.ToEventName()
        };
    }
}
