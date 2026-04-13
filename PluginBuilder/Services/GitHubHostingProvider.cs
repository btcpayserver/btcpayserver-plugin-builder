using System.Text.RegularExpressions;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PluginBuilder.APIModels;
using PluginBuilder.DataModels;
using PluginBuilder.JsonConverters;

namespace PluginBuilder.Services;

public class GitHubHostingProvider : IGitHostingProvider
{
    private static readonly Regex HostRegex = new(
        @"^https?://(www\.)?github\.com/", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RepoRegex = new(
        @"^https://(www\.)?github\.com/([^/]+)/([^/]+?)(?:\.git)?/?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IHttpClientFactory _httpClientFactory;

    public GitHubHostingProvider(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public bool CanHandle(string repoUrl) => HostRegex.IsMatch(repoUrl);

    public (string Owner, string RepoName)? ParseRepository(string repoUrl)
    {
        if (string.IsNullOrEmpty(repoUrl))
            return null;
        var match = RepoRegex.Match(repoUrl.Trim());
        if (!match.Success)
            return null;
        return (match.Groups[2].Value, match.Groups[3].Value);
    }

    public string? GetSourceUrl(string repoUrl, string? commit, string? pluginDir)
    {
        if (commit is null)
            return null;

        var repo = ParseRepository(repoUrl);
        if (repo is null)
        {
            // Handle git@ and other URL variants for backward compat
            return GetSourceUrlFromRawUrl(repoUrl, commit, pluginDir);
        }

        var link = $"https://github.com/{repo.Value.Owner}/{repo.Value.RepoName}/tree/{commit}";
        if (!string.IsNullOrEmpty(pluginDir))
            link += $"/{pluginDir}";
        return link;
    }

    public async Task<string> FetchIdentifierFromCsprojAsync(string repoUrl, string gitRef, string? pluginDir = null)
    {
        var client = _httpClientFactory.CreateClient(HttpClientNames.GitHub);
        var (owner, repoName) = ExtractOwnerRepo(repoUrl);
        var dir = string.IsNullOrWhiteSpace(pluginDir) ? "" : pluginDir.Trim('/');

        var encodedDir = string.Join('/', dir.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        var apiUrl = $"repos/{owner}/{repoName}/contents" + (string.IsNullOrEmpty(encodedDir) ? "" : "/" + encodedDir);

        if (!string.IsNullOrWhiteSpace(gitRef))
            apiUrl += $"?ref={Uri.EscapeDataString(gitRef.Trim())}";

        using var resp = await client.GetAsync(apiUrl);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new BuildServiceException(
                $"GitHub error ({(int)resp.StatusCode}) listing '{(string.IsNullOrEmpty(dir) ? "/" : dir)}': {apiUrl}\nBody: {body}");

        if (body.TrimStart().StartsWith('{'))
            throw new BuildServiceException(
                $"Expected directory listing but GitHub returned an object. Check pluginDir='{pluginDir}' (must be a directory). Path: {apiUrl}");

        var items = SafeJson.Deserialize<List<GithubContentItem>>(body);
        var csprojs = items
            .Where(i => string.Equals(i.type, "file", StringComparison.OrdinalIgnoreCase)
                        && i.name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).ToList();

        if (csprojs == null || csprojs.Count == 0)
            throw new BuildServiceException(
                $"No .csproj found in '{(string.IsNullOrEmpty(dir) ? "/" : dir)}' at {(string.IsNullOrWhiteSpace(gitRef) ? "default branch" : gitRef)}.");
        if (csprojs.Count > 1)
            throw new BuildServiceException("Multiple .csproj found. Keep exactly one.");

        var downloadUrl = csprojs[0].download_url;
        if (string.IsNullOrWhiteSpace(downloadUrl))
            throw new BuildServiceException($"GitHub item '{csprojs[0].name}' has no download_url.");

        using var csprojResp = await client.GetAsync(downloadUrl);
        var csprojBody = await csprojResp.Content.ReadAsStringAsync();

        if (!csprojResp.IsSuccessStatusCode || string.IsNullOrWhiteSpace(csprojBody))
            throw new BuildServiceException(
                $"GitHub error downloading '{csprojs[0].name}' from {downloadUrl} (HTTP {(int)csprojResp.StatusCode}).\nBody: {csprojBody}");

        var doc = XDocument.Parse(csprojBody);
        var assemblyName = doc.Descendants("AssemblyName").FirstOrDefault()?.Value ?? Path.GetFileNameWithoutExtension(csprojs[0].name);

        return assemblyName;
    }

    public async Task<List<GitHubContributor>> GetContributorsAsync(string repoUrl, string pluginDir)
    {
        var repo = ParseRepository(repoUrl);
        if (repo == null)
            return new List<GitHubContributor>();

        var client = _httpClientFactory.CreateClient(HttpClientNames.GitHub);
        var pathQuery = string.IsNullOrEmpty(pluginDir) ? "" : $"&path={Uri.EscapeDataString(pluginDir)}";
        var contributors = new Dictionary<string, GitHubContributor>(StringComparer.OrdinalIgnoreCase);
        int page = 1;
        const int perPage = 100;
        try
        {
            while (true)
            {
                var apiPath = $"repos/{repo.Value.Owner}/{repo.Value.RepoName}/commits?per_page={perPage}&page={page}{pathQuery}";
                using var response = await client.GetAsync(apiPath);
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

                if (!response.Headers.TryGetValues("Link", out var linkHeaders) || !linkHeaders.Any(l => l.Contains("rel=\"next\"")))
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

    private static (string owner, string repo) ExtractOwnerRepo(string repoUrl)
    {
        repoUrl = repoUrl.Trim().Replace(".git", "");

        if (!repoUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            repoUrl = "https://" + repoUrl.TrimStart('/');

        if (!Uri.TryCreate(repoUrl, UriKind.Absolute, out var uri))
            throw new BuildServiceException("Invalid repository URL");

        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length < 2)
            throw new BuildServiceException("Invalid repository URL");
        return (parts[0], parts[1]);
    }

    private static string? GetSourceUrlFromRawUrl(string repo, string commit, string? pluginDir)
    {
        string? repoName = null;
        // git@github.com:Kukks/btcpayserver.git
        if (repo.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
            repoName = repo.Substring("git@github.com:".Length);
        // https://github.com/Kukks/btcpayserver.git
        // https://github.com/Kukks/btcpayserver
        else if (repo.StartsWith("https://github.com/"))
            repoName = repo.Substring("https://github.com/".Length);

        if (repoName is null)
            return null;

        if (repoName.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            repoName = repoName.Substring(0, repoName.Length - 4);

        var link = $"https://github.com/{repoName}/tree/{commit}";
        if (!string.IsNullOrEmpty(pluginDir))
            link += $"/{pluginDir}";
        return link;
    }

    private record GithubContentItem(string name, string type, string? download_url);
}
