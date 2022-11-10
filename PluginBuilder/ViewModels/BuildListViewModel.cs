#nullable disable
namespace PluginBuilder.ViewModels
{
    public class BuildListViewModel
    {
        public class BuildViewModel
        {
            public long BuildId { get; set; }
            public string Date { get; set; }
            public string Version { get; set; }
            public string State { get; set; }
            public string Repository { get; set; }
            public string GitRef { get; set; }
            public string Commit { get; set; }
            public string RepositoryLink { get; set; }
            public string DownloadLink { get; set; }
            public string Error { get; set; }
            public bool Published { get; set; }
        }

        public List<BuildViewModel> Builds { get; set; } = new List<BuildViewModel>();
    }
}
