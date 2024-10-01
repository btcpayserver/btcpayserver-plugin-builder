#nullable disable
using Newtonsoft.Json.Linq;

namespace PluginBuilder.APIModels
{
    public class PublishedVersion
    {
        public string ProjectSlug { get; set; }
        public string Version { get; set; }
        public long BuildId { get; set; }
        public long DownloadStat { get; set; }
        public JObject BuildInfo { get; set; }
        public JObject ManifestInfo { get; set; }
        public string Documentation { get; set; }
    }
}
