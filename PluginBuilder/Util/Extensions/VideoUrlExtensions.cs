using System.Text.RegularExpressions;

namespace PluginBuilder.Util.Extensions;

// Extension methods for handling video URLs from various platforms.
public static partial class VideoUrlExtensions
{
    // regex video ID validation
    [GeneratedRegex(@"^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled)]
    private static partial Regex YoutubeIdRegex();

    [GeneratedRegex(@"^\d+$", RegexOptions.Compiled)]
    private static partial Regex VimeoIdRegex();

    // checks if the video URL is from a supported video platform (YouTube, Vimeo)
    public static bool IsSupportedVideoUrl(this string? videoUrl)
    {
        return videoUrl.GetVideoEmbedUrl() != null;
    }

    // converts video URL (YouTube, Vimeo) to its embeddable iframe format
    public static string? GetVideoEmbedUrl(this string? videoUrl)
    {
        if (string.IsNullOrWhiteSpace(videoUrl))
            return null;

        if (!Uri.TryCreate(videoUrl, UriKind.Absolute, out var uri))
            return null;

        if (uri.Scheme != Uri.UriSchemeHttps)
            return null;

        return TryGetYoutubeEmbedUrl(uri)
            ?? TryGetYoutubeShortEmbedUrl(uri)
            ?? TryGetVimeoEmbedUrl(uri);
    }

    // extract & build YouTube embed URL from youtube.com
    private static string? TryGetYoutubeEmbedUrl(Uri uri)
    {
        if (!IsHost(uri, "youtube.com"))
            return null;

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var videoId = query["v"];

        if (string.IsNullOrWhiteSpace(videoId) || !YoutubeIdRegex().IsMatch(videoId))
            return null;

        return $"https://www.youtube.com/embed/{videoId}";
    }

    // Attempts to extract and build a YouTube embed URL from youtu.be URLs.
    private static string? TryGetYoutubeShortEmbedUrl(Uri uri)
    {
        if (!IsHost(uri, "youtu.be"))
            return null;

        var videoId = uri.AbsolutePath.TrimStart('/').Split('?')[0]; // Remove query params

        if (string.IsNullOrWhiteSpace(videoId) || !YoutubeIdRegex().IsMatch(videoId))
            return null;

        return $"https://www.youtube.com/embed/{videoId}";
    }

    // Attempts to extract and build a Vimeo embed URL.
    private static string? TryGetVimeoEmbedUrl(Uri uri)
    {
        if (!IsHost(uri, "vimeo.com"))
            return null;

        var videoId = uri.AbsolutePath.TrimStart('/').Split('/')[0]; // Get first path segment

        if (string.IsNullOrWhiteSpace(videoId) || !VimeoIdRegex().IsMatch(videoId))
            return null;

        return $"https://player.vimeo.com/video/{videoId}";
    }

    // Checks if the URI host matches the specified host (including subdomains).
    private static bool IsHost(Uri uri, string host)
    {
        return uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase);
    }
}

