using Newtonsoft.Json.Linq;
using System.Net.Http;

namespace PluginBuilder.Services;

public class ExternalAccountVerificationService
{
    private readonly IHttpClientFactory _httpClientFactory;
    public ExternalAccountVerificationService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }
    public async Task<bool> VerifyGistToken(string profileUrl, string gistId, string token)
    {
        var client = _httpClientFactory.CreateClient();
        string username = ExtractGitHubUsername(profileUrl);
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
