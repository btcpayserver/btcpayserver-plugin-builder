using System.Text.RegularExpressions;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PluginBuilder.APIModels;
using PluginBuilder.DataModels;

namespace PluginBuilder.Services;

public class GitLabHostingProvider : IGitHostingProvider
{
    private static readonly Regex HostRegex = new(
        @"^https?://(www\.)?gitlab\.com/", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IEnumerable<string> _additionalHosts;

    public GitLabHostingProvider(IHttpClientFactory httpClientFactory, IEnumerable<string>? additionalHosts = null)
    {
        _httpClientFactory = httpClientFactory;
        _additionalHosts = additionalHosts ?? Enumerable.Empty<string>();
    }

    public bool CanHandle(string repoUrl)
    {
        if (HostRegex.IsMatch(repoUrl))
            return true;

        // Support self-hosted GitLab instances via configured additional hosts
        if (!Uri.TryCreate(repoUrl, UriKind.Absolute, out var uri))
            return false;

        return _additionalHosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase));
    }

    public (string Owner, string RepoName)? ParseRepository(string repoUrl)
    {
        if (string.IsNullOrEmpty(repoUrl))
            return null;

        if (!Uri.TryCreate(repoUrl.Trim(), UriKind.Absolute, out var uri))
            return null;

        var path = uri.AbsolutePath.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            return null;

        // Last segment is the repo name, everything before is the namespace/owner
        var repoName = segments[^1];
        var owner = string.Join("/", segments[..^1]);
        return (owner, repoName);
    }

    public string? GetSourceUrl(string repoUrl, string? commit, string? pluginDir)
    {
        if (commit is null)
            return null;

        if (!Uri.TryCreate(repoUrl.Trim(), UriKind.Absolute, out var uri))
            return null;

        var path = uri.AbsolutePath.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];

        var baseUrl = $"{uri.Scheme}://{uri.Authority}/{path}";
        var link = $"{baseUrl}/-/tree/{commit}";
        if (!string.IsNullOrEmpty(pluginDir))
            link += $"/{pluginDir}";
        return link;
    }

    public async Task<string> FetchIdentifierFromCsprojAsync(string repoUrl, string gitRef, string? pluginDir = null)
    {
        var client = CreateClientForRepo(repoUrl);
        var projectId = GetProjectId(repoUrl);
        var dir = string.IsNullOrWhiteSpace(pluginDir) ? "" : pluginDir.Trim('/');

        // List files in the directory using the Repository Tree API
        var apiUrl = $"projects/{Uri.EscapeDataString(projectId)}/repository/tree";
        var queryParams = new List<string>();
        if (!string.IsNullOrEmpty(dir))
            queryParams.Add($"path={Uri.EscapeDataString(dir)}");
        if (!string.IsNullOrWhiteSpace(gitRef))
            queryParams.Add($"ref={Uri.EscapeDataString(gitRef.Trim())}");
        if (queryParams.Count > 0)
            apiUrl += "?" + string.Join("&", queryParams);

        using var resp = await client.GetAsync(apiUrl);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new BuildServiceException(
                $"GitLab error ({(int)resp.StatusCode}) listing '{(string.IsNullOrEmpty(dir) ? "/" : dir)}': {apiUrl}\nBody: {body}");

        var items = JsonConvert.DeserializeObject<List<GitLabTreeItem>>(body);
        if (items == null)
            throw new BuildServiceException(
                $"Failed to parse GitLab tree response for '{(string.IsNullOrEmpty(dir) ? "/" : dir)}'.");

        var csprojs = items
            .Where(i => string.Equals(i.Type, "blob", StringComparison.OrdinalIgnoreCase)
                        && i.Name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).ToList();

        if (csprojs.Count == 0)
            throw new BuildServiceException(
                $"No .csproj found in '{(string.IsNullOrEmpty(dir) ? "/" : dir)}' at {(string.IsNullOrWhiteSpace(gitRef) ? "default branch" : gitRef)}.");
        if (csprojs.Count > 1)
            throw new BuildServiceException("Multiple .csproj found. Keep exactly one.");

        // Download the .csproj file content using the Repository Files API
        var filePath = csprojs[0].Path;
        var fileApiUrl = $"projects/{Uri.EscapeDataString(projectId)}/repository/files/{Uri.EscapeDataString(filePath)}/raw";
        if (!string.IsNullOrWhiteSpace(gitRef))
            fileApiUrl += $"?ref={Uri.EscapeDataString(gitRef.Trim())}";

        using var fileResp = await client.GetAsync(fileApiUrl);
        var csprojBody = await fileResp.Content.ReadAsStringAsync();

        if (!fileResp.IsSuccessStatusCode || string.IsNullOrWhiteSpace(csprojBody))
            throw new BuildServiceException(
                $"GitLab error downloading '{csprojs[0].Name}' (HTTP {(int)fileResp.StatusCode}).\nBody: {csprojBody}");

        XDocument doc;
        try
        {
            doc = XDocument.Parse(csprojBody);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new BuildServiceException($"Failed to parse '{csprojs[0].Name}' as XML: {ex.Message}");
        }
        var assemblyName = doc.Descendants("AssemblyName").FirstOrDefault()?.Value ?? Path.GetFileNameWithoutExtension(csprojs[0].Name);

        return assemblyName;
    }

    public async Task<List<GitHubContributor>> GetContributorsAsync(string repoUrl, string pluginDir)
    {
        var repo = ParseRepository(repoUrl);
        if (repo == null)
            return new List<GitHubContributor>();

        var client = CreateClientForRepo(repoUrl);
        var projectId = GetProjectId(repoUrl);
        var contributors = new Dictionary<string, GitHubContributor>(StringComparer.OrdinalIgnoreCase);
        int page = 1;
        const int perPage = 100;

        try
        {
            while (true)
            {
                var apiPath = $"projects/{Uri.EscapeDataString(projectId)}/repository/commits?per_page={perPage}&page={page}";
                if (!string.IsNullOrEmpty(pluginDir))
                    apiPath += $"&path={Uri.EscapeDataString(pluginDir)}";

                using var response = await client.GetAsync(apiPath);
                if (!response.IsSuccessStatusCode)
                    break;

                var json = await response.Content.ReadAsStringAsync();
                var commits = JsonConvert.DeserializeObject<List<GitLabCommit>>(json);
                if (commits == null || commits.Count == 0)
                    break;

                foreach (var commit in commits)
                {
                    var name = commit.AuthorName;
                    if (string.IsNullOrEmpty(name))
                        continue;

                    // Use author email as key since GitLab commits don't have user login
                    var key = commit.AuthorEmail ?? name;
                    if (contributors.TryGetValue(key, out var existing))
                    {
                        existing.Contributions++;
                    }
                    else
                    {
                        contributors[key] = new GitHubContributor
                        {
                            Login = name,
                            AvatarUrl = null,
                            HtmlUrl = null,
                            Contributions = 1
                        };
                    }
                }

                // Check for next page via header
                if (!response.Headers.TryGetValues("x-next-page", out var nextPageHeaders))
                    break;
                var nextPage = nextPageHeaders.FirstOrDefault();
                if (string.IsNullOrEmpty(nextPage) || !int.TryParse(nextPage, out var np) || np <= page)
                    break;

                page = np;
            }

            var result = contributors.Values.OrderByDescending(c => c.Contributions).ToList();
            await ResolveAvatarsAsync(client, contributors);
            return result;
        }
        catch (Exception)
        {
            return new List<GitHubContributor>();
        }
    }

    private static async Task ResolveAvatarsAsync(
        HttpClient client,
        Dictionary<string, GitHubContributor> contributors)
    {
        foreach (var (key, contributor) in contributors)
        {
            // key is the email when available
            if (!key.Contains('@'))
                continue;
            try
            {
                var apiUrl = $"avatar?email={Uri.EscapeDataString(key)}&size=48";
                using var resp = await client.GetAsync(apiUrl);
                if (!resp.IsSuccessStatusCode)
                    continue;
                var json = await resp.Content.ReadAsStringAsync();
                var obj = JObject.Parse(json);
                var avatarUrl = obj["avatar_url"]?.ToString();
                if (!string.IsNullOrWhiteSpace(avatarUrl))
                    contributor.AvatarUrl = avatarUrl;
            }
            catch
            {
                // Best effort — skip if anything goes wrong
            }
        }
    }

    private HttpClient CreateClientForRepo(string repoUrl)
    {
        if (!Uri.TryCreate(repoUrl.Trim(), UriKind.Absolute, out var uri))
            throw new BuildServiceException("Invalid repository URL");

        // Use the named GitLab client so GITLAB_TOKEN config is applied
        var client = _httpClientFactory.CreateClient(HttpClientNames.GitLab);
        // Override base address to target the repo's actual host (self-hosted support)
        client.BaseAddress = new Uri($"{uri.Scheme}://{uri.Authority}/api/v4/");
        return client;
    }

    private static string GetProjectId(string repoUrl)
    {
        if (!Uri.TryCreate(repoUrl.Trim(), UriKind.Absolute, out var uri))
            throw new BuildServiceException("Invalid repository URL");

        var path = uri.AbsolutePath.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];

        if (string.IsNullOrEmpty(path))
            throw new BuildServiceException("Invalid repository URL");

        // GitLab project ID is the URL-encoded full path (e.g., "group/subgroup/repo")
        return path;
    }

    private record GitLabTreeItem(
        [property: JsonProperty("name")] string Name,
        [property: JsonProperty("type")] string Type,
        [property: JsonProperty("path")] string Path);

    private record GitLabCommit(
        [property: JsonProperty("author_name")] string AuthorName,
        [property: JsonProperty("author_email")] string? AuthorEmail);
}
