using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PluginBuilder.APIModels;

namespace PluginBuilder.Services;

public static class GithubService
{
    private static readonly Regex GithubRepositoryRegex = new("^https://(www\\.)?github\\.com/([^/]+)/([^/]+)/?");

    public static async Task<List<GitHubContributor>> GetContributorsAsync(HttpClient githubClient, string gitRepository, string pluginDir)
    {
        var repo = ParseRepository(gitRepository);
        if (repo == null)
            return new List<GitHubContributor>();

        var pathQuery = string.IsNullOrEmpty(pluginDir) ? "" : $"&path={Uri.EscapeDataString(pluginDir)}";
        var contributors = new Dictionary<string, GitHubContributor>(StringComparer.OrdinalIgnoreCase);
        int page = 1;
        const int perPage = 100;
        const int maxPages = 50;
        try
        {
            while (page <= maxPages)
            {
                var apiPath = $"repos/{repo.Value.Owner}/{repo.Value.RepoName}/commits?per_page={perPage}&page={page}{pathQuery}";
                using var response = await githubClient.GetAsync(apiPath);
                if (!response.IsSuccessStatusCode)
                    break;

                var json = await response.Content.ReadAsStringAsync();
                var commits = JsonConvert.DeserializeObject<List<GitHubCommit>>(json);
                if (commits == null || commits.Count == 0)
                    break;

                foreach (var commit in commits)
                {
                    var login = commit.Author?.Login;
                    if (commit.Author == null || string.IsNullOrEmpty(login))
                        continue;

                    if (contributors.TryGetValue(login, out var existing))
                    {
                        existing.Contributions++;
                    }
                    else
                    {
                        contributors[login] = new GitHubContributor
                        {
                            Login = commit.Author.Login,
                            AvatarUrl = commit.Author.AvatarUrl,
                            HtmlUrl = commit.Author.HtmlUrl,
                            Contributions = 1
                        };
                    }
                }
                if (commits.Count < perPage)
                    break;

                page++;
            }
            return contributors.Values.OrderByDescending(c => c.Contributions).ToList();
        }
        catch (Exception)
        {
            return new List<GitHubContributor>();
        }
    }

    public static async Task SaveSnapshotAsync(PluginSlug pluginSlug, List<GitHubContributor> contributors)
    {
        var dir = Path.Combine(Directory.GetCurrentDirectory(), "PluginData");
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, $"{pluginSlug}.json");
        var data = new JObject
        {
            ["contributors"] = JArray.FromObject(contributors)
        };
        await File.WriteAllTextAsync(filePath, data.ToString(Formatting.Indented));
    }

    public static List<GitHubContributor> LoadSnapshot(PluginSlug pluginSlug)
    {
        var filePath = Path.Combine(Directory.GetCurrentDirectory(), "PluginData", $"{pluginSlug}.json");
        if (!File.Exists(filePath))
            return new List<GitHubContributor>();

        var json = File.ReadAllText(filePath);
        var data = JObject.Parse(json);
        return data["contributors"]?.ToObject<List<GitHubContributor>>() ?? new List<GitHubContributor>();
    }

    private static (string Owner, string RepoName)? ParseRepository(string gitRepository)
    {
        if (string.IsNullOrEmpty(gitRepository))
            return null;
        var match = GithubRepositoryRegex.Match(gitRepository);
        if (!match.Success)
            return null;
        return (match.Groups[2].Value, match.Groups[3].Value);
    }
}
