#nullable disable
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PluginBuilder.APIModels;

public class PublishedVersion
{
    public string ProjectSlug { get; set; }
    public string Version { get; set; }
    public long BuildId { get; set; }
    public JObject BuildInfo { get; set; }
    public JObject ManifestInfo { get; set; }
    public string PluginLogo { get; set; }
    public string Documentation { get; set; }
    public string PluginTitle { get; set; }
    public string Description { get; set; }
    public string Fingerprint { get; set; }
}

public class PublishedPlugin : PublishedVersion
{
    private static readonly Regex GithubRepositoryRegex = new("^https://(www\\.)?github\\.com/([^/]+)/([^/]+)/?");
    public DateTimeOffset CreatedDate { get; set; }

    public string gitRepository
    {
        get => BuildInfo?["gitRepository"]?.ToString();
    }

    public string pluginDir
    {
        get => BuildInfo?["pluginDir"]?.ToString();
    }

    public PluginRatingSummary RatingSummary { get; set; } = new();

    public GithubRepository GetGithubRepository()
    {
        if (gitRepository is null)
            return null;
        var match = GithubRepositoryRegex.Match(gitRepository);
        if (!match.Success)
            return null;
        return new GithubRepository(match.Groups[2].Value, match.Groups[3].Value);
    }

    public record GithubRepository(string Owner, string RepositoryName)
    {
        public string GetSourceUrl(string commit, string pluginDir)
        {
            if (commit is null)
                return null;
            return $"https://github.com/{Owner}/{RepositoryName}/tree/{commit}/{pluginDir}";
        }
    }
}

public class GitHubCommit
{
    [JsonProperty("author")]
    public GitHubContributor Author { get; set; }
}

public class GitHubContributor
{
    [JsonProperty("login")]
    public string Login { get; set; }

    [JsonProperty("avatar_url")]
    public string AvatarUrl { get; set; }

    [JsonProperty("html_url")]
    public string HtmlUrl { get; set; }

    [JsonProperty("user_view_type")]
    public string UserViewType { get; set; }

    [JsonProperty("contributions")]
    public int Contributions { get; set; }
}

public class PluginRatingSummary
{
    public decimal Average { get; set; }
    public int TotalReviews { get; set; }

    public int C1 { get; set; }
    public int C2 { get; set; }
    public int C3 { get; set; }
    public int C4 { get; set; }
    public int C5 { get; set; }

    [JsonIgnore] public IReadOnlyDictionary<int, int> StarCounts
    {
        get => new Dictionary<int, int>
        {
            [1] = C1, [2] = C2, [3] = C3, [4] = C4, [5] = C5
        };
    }

    [JsonIgnore] public IReadOnlyDictionary<int, int> StarPct
    {
        get
        {
            var total = Math.Max(TotalReviews, 0);
            return new Dictionary<int, int>
            {
                [1] = Pct(C1), [2] = Pct(C2), [3] = Pct(C3), [4] = Pct(C4), [5] = Pct(C5)
            };

            int Pct(int c)
            {
                return total == 0 ? 0 : (int)Math.Round(c * 100.0 / total);
            }
        }
    }
}
