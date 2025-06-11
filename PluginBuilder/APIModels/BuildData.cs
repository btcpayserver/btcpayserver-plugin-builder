#nullable disable
using PluginBuilder.Util;

namespace PluginBuilder.APIModels;

public class BuildData
{
    public string ProjectSlug { get; set; }
    public long BuildId { get; set; }
    public BuildInfo BuildInfo { get; set; }
    public PluginManifest ManifestInfo { get; set; }
    public DateTimeOffset CreatedDate { get; set; }
    public string DownloadLink { get; set; }
    public bool Published { get; set; }
    public bool Prerelease { get; set; }
    public string Commit { get; set; }
    public string Repository { get; set; }
    public string GitRef { get; set; }
}
