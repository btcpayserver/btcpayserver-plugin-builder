#nullable disable
namespace PluginBuilder.Components.PluginVersion
{
    public class PluginVersionViewModel
    {
        public static PluginVersionViewModel CreateOrNull(string version, bool published, bool pre_release)
        {
            if (version is null)
                return null;
            return new PluginVersionViewModel()
            {
                Version = version,
                Published = published,
                PreRelease = pre_release
            };
        }
        public string Version { get; set; }
        public bool Published { get; set; }
        public bool PreRelease { get; set; }
        public bool HidePublishBadge { get; set; }
    }
}
