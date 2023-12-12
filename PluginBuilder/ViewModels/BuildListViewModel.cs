#nullable disable
using PluginBuilder.Components.PluginVersion;

namespace PluginBuilder.ViewModels
{
    public class BuildListViewModel
    {
        public class BuildViewModel
        {
            public PluginVersionViewModel Version { get; set; }
            public long BuildId { get; set; }
            public string Date { get; set; }
            public string State { get; set; }
            public string Repository { get; set; }
            public string GitRef { get; set; }
            public string Commit { get; set; }
            public string RepositoryLink { get; set; }
            public string DownloadLink { get; set; }
            public string Error { get; set; }
            public string PluginSlug { get; set; }
            public string PluginIdentifier { get; set; }
        }

        public List<BuildViewModel> Builds { get; set; } = new List<BuildViewModel>();
    }
}
