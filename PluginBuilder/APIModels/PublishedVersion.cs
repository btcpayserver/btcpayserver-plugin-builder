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
}

public class PublishedPlugin : PublishedVersion
{
    public DateTimeOffset CreatedDate { get; set; }
    static Regex GithubRepositoryRegex = new Regex("^https://(www\\.)?github\\.com/([^/]+)/([^/]+)/?");
    public string gitRepository => BuildInfo?["gitRepository"]?.ToString();
    public string pluginDir => BuildInfo?["pluginDir"]?.ToString();
    public record GithubRepository(string Owner, string RepositoryName)
    {
        public string GetSourceUrl(string commit, string pluginDir)
        {
            if (commit is null)
                return null;
            return $"https://github.com/{Owner}/{RepositoryName}/tree/{commit}/{pluginDir}";
        }
    }
    public GithubRepository GetGithubRepository()
    {
        if (gitRepository is null)
            return null;
        var match = GithubRepositoryRegex.Match(gitRepository);
        if (!match.Success)
            return null;
        return new GithubRepository(match.Groups[2].Value, match.Groups[3].Value);
    }

    public async Task<List<GitHubContributor>> GetContributorsAsync(HttpClient httpClient, string pluginDir)
    {
        var repo = GetGithubRepository();
        if (repo == null)
            return new();

        string url = string.IsNullOrEmpty(pluginDir)
            ? $"https://api.github.com/repos/{repo.Owner}/{repo.RepositoryName}/commits"
            : $"https://api.github.com/repos/{repo.Owner}/{repo.RepositoryName}/commits?path={pluginDir}";
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("PluginBuilder");
            using var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return new();

            var json = await response.Content.ReadAsStringAsync();
            var commits = JArray.Parse(json);

            return commits
                .Select(c => c["author"])
                .Where(a => a != null && a["login"] != null)
                .GroupBy(a => a["login"]!.ToString())
                .Select(g => new GitHubContributor
                {
                    Login = g.Key,
                    AvatarUrl = g.First()?["avatar_url"]?.ToString(),
                    HtmlUrl = g.First()?["html_url"]?.ToString(),
                    Contributions = g.Count()
                })
                .ToList();
        }
        catch (Exception)
        {
            return new();
        }
    }
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

