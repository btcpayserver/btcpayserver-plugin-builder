#nullable disable


namespace PluginBuilder.Components.MainNav
{
    public class MainNavViewModel
    {
        public string PluginSlug { get; set; }

        public List<string> Versions { get; set; } = new List<string>();
    }
}
