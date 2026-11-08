using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PluginBuilder.DataModels;

public sealed class ListingReviewTarget
{
    public long BuildId { get; set; }
    public string? Version { get; set; }
    public bool? PreRelease { get; set; }
    public string Repository { get; set; } = "";
    public string GitCommit { get; set; } = "";
    public string? PluginDirectory { get; set; }

    // Keep build zero, the stable flag and the repository-root directory explicit.
    public string ToJson() => new JObject
    {
        ["buildId"] = BuildId, ["repository"] = Repository, ["gitCommit"] = GitCommit,
        ["pluginDirectory"] = PluginDirectory, ["version"] = Version, ["preRelease"] = PreRelease
    }.ToString(Formatting.None);

    public static ListingReviewTarget? FromJson(string? json) =>
        json is null ? null : JsonConvert.DeserializeObject<ListingReviewTarget>(json);
}
