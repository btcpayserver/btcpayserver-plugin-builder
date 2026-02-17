using System.Text.RegularExpressions;

namespace PluginBuilder.Util.Extensions;

public static class VideoUrlExtensions
{
    private static readonly string[] SupportedVideoHosts =
    [
        "youtube.com",
        "youtu.be",
        "vimeo.com"
    ];
    private static readonly Regex YoutubeIdRegex = new(@"^[\w-]{11}$", RegexOptions.Compiled);
    private static readonly Regex VimeoIdRegex = new(@"^\d+$", RegexOptions.Compiled);

    // checks if the video URL is from a supported video platform
    public static bool IsSupportedVideoUrl(this string? videoUrl)
    {
        if (string.IsNullOrWhiteSpace(videoUrl))
            return false;

        try
        {
            var uri = new Uri(videoUrl);
            return IsSupportedVideoUrl(uri);
        }
        catch
        {
            return false;
        }
    }

    // checks if the video URI is from a supported video platform
    private static bool IsSupportedVideoUrl(Uri uri)
    {
        return uri != null && SupportedVideoHosts.Any(host =>
            uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));
    }

    // converts a video URL to its embeddable format
    public static string? GetVideoEmbedUrl(this string? videoUrl)
    {
        if (string.IsNullOrWhiteSpace(videoUrl)) return null;

        if (!Uri.TryCreate(videoUrl, UriKind.Absolute, out var uri)) return null;

        if (uri.Scheme != Uri.UriSchemeHttps) return null;

        if (IsHost("youtube.com"))
        {
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var videoId = query["v"];
            if (!string.IsNullOrWhiteSpace(videoId) && YoutubeIdRegex.IsMatch(videoId)) return $"https://www.youtube.com/embed/{videoId}";
        }
        else if (IsHost("youtu.be"))
        {
            var videoId = uri.AbsolutePath.TrimStart('/');
            if (!string.IsNullOrWhiteSpace(videoId) && YoutubeIdRegex.IsMatch(videoId)) return $"https://www.youtube.com/embed/{videoId}";
        }
        else if (IsHost("vimeo.com"))
        {
            var videoId = uri.AbsolutePath.TrimStart('/');
            if (!string.IsNullOrWhiteSpace(videoId) && VimeoIdRegex.IsMatch(videoId)) return $"https://player.vimeo.com/video/{videoId}";
        }
        return null;

        bool IsHost(string host) =>
            uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase);
    }
}
