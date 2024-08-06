namespace PluginBuilder.ViewModels
{
    public class AvailablePluginViewModel
    {
        public string Identifier { get; set; }
        public string Name { get; set; }
        public Version Version { get; set; }
        public string Description { get; set; }
        public bool SystemPlugin { get; set; } = false;
        public DateTime BuildDate { get; set; }
        public long? DownloadStat { get; set; }
        public string Documentation { get; set; }
        public string Source { get; set; }
        public string Author { get; set; }
        public string PluginLogo { get; set; }
        public string AuthorLink { get; set; }
        public string AuthorNostr { get; set; }
        public string AuthorTwitter { get; set; }
        public string AuthorEmail { get; set; }
    }
}
