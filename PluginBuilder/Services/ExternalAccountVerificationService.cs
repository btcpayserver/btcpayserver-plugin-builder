using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PluginBuilder.APIModels;
using PluginBuilder.DataModels;

namespace PluginBuilder.Services;

public sealed record VerifiedGithubIdentity(long UserId, string Login);

public class ExternalAccountVerificationService(IHttpClientFactory httpClientFactory)
{
    public Task<VerifiedGithubIdentity?> VerifyGistToken(string gistUrl, string token, CancellationToken cancellationToken = default) =>
        VerifyGist(gistUrl, token, allowRenamedOwner: false, cancellationToken);

    // Only for a previously stored proof: the gist ID and account token still bind the owner after a rename.
    public Task<VerifiedGithubIdentity?> RecoverGistIdentity(string gistUrl, string token, CancellationToken cancellationToken = default) =>
        VerifyGist(gistUrl, token, allowRenamedOwner: true, cancellationToken);

    private async Task<VerifiedGithubIdentity?> VerifyGist(string gistUrl, string token, bool allowRenamedOwner, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(gistUrl))
            return null;

        // Match the supplied URL before URI normalization can select a different gist in an old proof.
        var match = Regex.Match(gistUrl, @"\Ahttps://gist\.github\.com(?::443)?/([a-zA-Z0-9-]+)/([a-fA-F0-9]+)/?(?:[?#][^\r\n]*)?\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return null;

        try
        {
            using var client = httpClientFactory.CreateClient(HttpClientNames.GitHub);
            using var response = await client.GetAsync($"gists/{match.Groups[2].Value}", cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            var gistData = JObject.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (gistData["owner"] is not JObject owner ||
                owner["id"]?.Type != JTokenType.Integer ||
                !long.TryParse(owner["id"]!.ToString(), out var userId) || userId <= 0 ||
                owner["login"]?.Type != JTokenType.String ||
                string.IsNullOrWhiteSpace(owner.Value<string>("login")))
                return null;

            var login = owner.Value<string>("login")!;
            if (!allowRenamedOwner && !login.Equals(match.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
                return null;

            if (gistData["files"] is not JObject files)
                return null;
            foreach (var file in files.Properties())
            {
                if (file.Value is JObject data && data["content"]?.Type == JTokenType.String &&
                    data.Value<string>("content")!.Contains(token, StringComparison.Ordinal))
                    return new VerifiedGithubIdentity(userId, login);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException ||
                                   ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        return null;
    }

    public static string? GetGithubHandle(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
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
        if (segs.Length == 0)
            return null;

        var handle = segs[0];
        if (handle.Equals("orgs", StringComparison.OrdinalIgnoreCase) ||
            handle.Equals("users", StringComparison.OrdinalIgnoreCase))
            return null;

        return handle;
    }

    public static GitHubContributor? GetGithubIdentity(string? githubUrl, int size = 48)
    {
        var handle = GetGithubHandle(githubUrl);
        if (string.IsNullOrWhiteSpace(handle))
            return null;

        var safe = Uri.EscapeDataString(handle);
        return new GitHubContributor
        {
            Login = handle,
            HtmlUrl = $"{ExternalProfileUrls.GithubBaseUrl}{safe}",
            AvatarUrl = string.Format(ExternalProfileUrls.GithubAvatarFormat, safe, size),
            UserViewType = "user",
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

    public const string UnavatarSiteFormat = "https://unavatar.io/{0}";
}
