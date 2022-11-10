#nullable disable
namespace PluginBuilder.ViewModels
{
    public class BuildViewModel
    {
        public FullBuildId FullBuildId { get; set; }
        public string ManifestInfo { get; internal set; }
        public string BuildInfo { get; internal set; }
        public string CreatedDate { get; set; }
        public string DownloadLink { get; set; }
        public bool Published { get; set; }
        public string Commit { get; internal set; }
        public string Repository { get; internal set; }
        public string GitRef { get; internal set; }
        public string Version { get; internal set; }
        public string RepositoryLink { get; internal set; }
        public string Logs { get; set; }
    }
}
