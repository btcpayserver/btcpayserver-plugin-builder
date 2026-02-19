using System.Text.RegularExpressions;

namespace PluginBuilder.Util.Extensions;

public static partial class VideoUrlExtensions
{
    [GeneratedRegex(@"^[A-Za-z0-9_-]{11}$")]
    private static partial Regex YoutubeIdRegex();

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex VimeoIdRegex();

    public static bool IsSupportedVideoUrl(this string? videoUrl)
    {
        return videoUrl.GetVideoEmbedUrl() != null;
    }

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

    private static string? TryGetYoutubeShortEmbedUrl(Uri uri)
    {
        if (!IsHost(uri, "youtu.be"))
            return null;

        var videoId = uri.AbsolutePath.TrimStart('/');
        if (string.IsNullOrWhiteSpace(videoId) || !YoutubeIdRegex().IsMatch(videoId))
            return null;

        return $"https://www.youtube.com/embed/{videoId}";
    }

    private static string? TryGetVimeoEmbedUrl(Uri uri)
    {
        if (!IsHost(uri, "vimeo.com"))
            return null;

        var videoId = uri.AbsolutePath.TrimStart('/').Split('/')[0];
        if (string.IsNullOrWhiteSpace(videoId) || !VimeoIdRegex().IsMatch(videoId))
            return null;

        return $"https://player.vimeo.com/video/{videoId}";
    }

    private static bool IsHost(Uri uri, string host)
    {
        return uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase);
    }
}

