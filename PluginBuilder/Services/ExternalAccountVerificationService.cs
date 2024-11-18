using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;

namespace PluginBuilder.Services;

public class ExternalAccountVerificationService
{
    private readonly IHttpClientFactory _httpClientFactory;
    public ExternalAccountVerificationService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }
    public async Task<bool> VerifyGistToken(string profileUrl, string gistUrl, string token)
    {
        var regex = new Regex(@"https://gist\.github\.com/([^/]+)/([^/]+)", RegexOptions.IgnoreCase);
        var match = regex.Match(gistUrl);
        if (!match.Success)
            return false;

        var gistUsername = match.Groups[1].Value;
        var gistId = match.Groups[2].Value;

        string username = ExtractGitHubUsername(profileUrl);
        if (!string.Equals(gistUsername, username, StringComparison.OrdinalIgnoreCase))
            return false;

        var client = _httpClientFactory.CreateClient();
        var url = $"https://api.github.com/gists/{gistId}";
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GitHubVerificationApp");
        var response = await client.GetAsync(url);
        if (!response.IsSuccessStatusCode)
            return false;

        var content = await response.Content.ReadAsStringAsync();
        var gistData = JObject.Parse(content);

        var owner = gistData["owner"]?["login"]?.ToString();
        if (owner == null || !owner.Equals(username, StringComparison.OrdinalIgnoreCase))
            return false;

        var files = gistData["files"];
        foreach (var file in files)
        {
            var fileContent = file.First["content"]?.ToString();
            if (fileContent != null && fileContent.Contains(token))
                return true;
        }
        return false;
    }

    public string ExtractGitHubUsername(string githubUrl)
    {
        if (Uri.TryCreate(githubUrl, UriKind.Absolute, out var uri) && uri.Host == "github.com")
        {
            var segments = uri.AbsolutePath.Trim('/').Split('/');
            return segments.Length > 0 ? segments[0] : null;
        }
        throw new ArgumentException("Invalid GitHub profile URL");
    }
}
