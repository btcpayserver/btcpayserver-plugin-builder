using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

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

        var client = httpClientFactory.CreateClient();
        var url = $"https://api.github.com/gists/{gistId}";
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GitHubVerificationApp");
        var response = await client.GetAsync(url);
        if (!response.IsSuccessStatusCode)
            return null;

        var content = await response.Content.ReadAsStringAsync();
        var gistData = JObject.Parse(content);

        var owner = gistData["owner"]?["login"]?.ToString();
        if (owner == null || !owner.Equals(gistUsername, StringComparison.OrdinalIgnoreCase))
            return null;

        var files = gistData["files"];
        foreach (var file in files)
        {
            var fileContent = file.First["content"]?.ToString();
            if (fileContent != null && fileContent.Contains(token))
                return gistUsername;
        }

        return null;
    }
}
