using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PluginBuilder.DataModels;

public sealed class ListingReviewTarget
{
    public long BuildId { get; set; }
    public string Repository { get; set; } = "";
    public string GitCommit { get; set; } = "";
    public string? PluginDirectory { get; set; }

    // Keep build zero and the repository-root directory explicit in the snapshot.
    public string ToJson() => new JObject
    {
        ["buildId"] = BuildId, ["repository"] = Repository, ["gitCommit"] = GitCommit,
        ["pluginDirectory"] = PluginDirectory
    }.ToString(Formatting.None);

    public static ListingReviewTarget? FromJson(string? json) =>
        json is null ? null : JsonConvert.DeserializeObject<ListingReviewTarget>(json);
}
