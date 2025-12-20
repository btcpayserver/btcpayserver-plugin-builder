using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using PluginBuilder.APIModels;
using PluginBuilder.DataModels;

namespace PluginBuilder.Services;

public class ExternalAccountVerificationService(IHttpClientFactory httpClientFactory)
{
    public async Task<string?> VerifyGistToken(string gistUrl, string token)
    {
        Regex regex = new(@"https://gist\.github\.com/([^/]+)/([^/]+)", RegexOptions.IgnoreCase);
        var match = regex.Match(gistUrl);
        if (!match.Success)
            return null;

        var gistUsername = match.Groups[1].Value;
        var gistId = match.Groups[2].Value;

        var client = httpClientFactory.CreateClient(HttpClientNames.GitHub);
        var response = await client.GetAsync($"gists/{gistId}");

        if (!response.IsSuccessStatusCode)
            return null;

        var content = await response.Content.ReadAsStringAsync();
        var gistData = JObject.Parse(content);

        var owner = gistData["owner"]?["login"]?.ToString();
        if (owner == null || !owner.Equals(gistUsername, StringComparison.OrdinalIgnoreCase))
            return null;

        var files = gistData["files"];
        if (files == null) return null;

        foreach (var file in files)
        {
            var fileContent = file.First?["content"]?.ToString();
            if (fileContent != null && fileContent.Contains(token, StringComparison.Ordinal))
                return gistUsername;
        }
        return null;
    }

    public static string? GetGithubHandle(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();

        if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            var raw = url.TrimStart('/');
            if (raw.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("www.github.com/", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate("https://" + raw, UriKind.Absolute, out u))
                    return null;
            }
            else
            {
                if (!Uri.TryCreate("https://github.com/" + raw, UriKind.Absolute, out u))
                    return null;
            }
        }

        if (!u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && !u.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase))
            return null;

        var segs = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segs.Length == 0) return null;

        var handle = segs[0];
        if (handle.Equals("orgs", StringComparison.OrdinalIgnoreCase) ||
            handle.Equals("users", StringComparison.OrdinalIgnoreCase))
            return null;

        return handle;
    }

    public static GitHubContributor? GetGithubIdentity(string? githubUrl, int size = 48)
    {
        var handle = GetGithubHandle(githubUrl);
        if (string.IsNullOrWhiteSpace(handle)) return null;

        var safe = Uri.EscapeDataString(handle);
        return new GitHubContributor
        {
            Login = handle,
            HtmlUrl = $"{ExternalProfileUrls.GithubBaseUrl}{safe}",
            AvatarUrl = string.Format(ExternalProfileUrls.GithubAvatarFormat, safe, size),
            UserViewType= "user",
            Contributions = 0
        };
    }
}

public static class ExternalProfileUrls
{
    public const string GithubBaseUrl = "https://github.com/";
    public const string GithubAvatarFormat = "https://github.com/{0}.png?size={1}";

    public const string XBaseUrl = "https://x.com/";
    public const string XAvatarFormat = "https://unavatar.io/twitter/{0}";

    public const string PrimalProfileFormat = "https://primal.net/p/{0}";

    public const string UnavatarSiteFormat  = "https://unavatar.io/{0}";
}
