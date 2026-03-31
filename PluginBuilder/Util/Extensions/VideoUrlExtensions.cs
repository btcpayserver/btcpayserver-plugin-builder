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

    public static string? GetVideoThumbnailUrl(this string? videoUrl)
    {
        if (string.IsNullOrWhiteSpace(videoUrl))
            return null;

        if (!Uri.TryCreate(videoUrl, UriKind.Absolute, out var uri))
            return null;

        if (uri.Scheme != Uri.UriSchemeHttps)
            return null;

        var youtubeId = TryGetYoutubeVideoId(uri);
        if (!string.IsNullOrEmpty(youtubeId))
            return $"https://i.ytimg.com/vi/{youtubeId}/hqdefault.jpg";

        var vimeoId = TryGetVimeoVideoId(uri);
        if (!string.IsNullOrEmpty(vimeoId))
            return $"https://vumbnail.com/{vimeoId}.jpg";

        return null;
    }

    private static string? TryGetYoutubeEmbedUrl(Uri uri)
    {
        var videoId = TryGetYoutubeVideoId(uri);
        if (string.IsNullOrWhiteSpace(videoId))
            return null;

        return $"https://www.youtube.com/embed/{videoId}";
    }

    private static string? TryGetYoutubeShortEmbedUrl(Uri uri)
    {
        var videoId = TryGetYoutubeShortVideoId(uri);
        if (string.IsNullOrWhiteSpace(videoId))
            return null;

        return $"https://www.youtube.com/embed/{videoId}";
    }

    private static string? TryGetVimeoEmbedUrl(Uri uri)
    {
        var videoId = TryGetVimeoVideoId(uri);
        if (string.IsNullOrWhiteSpace(videoId))
            return null;

        return $"https://player.vimeo.com/video/{videoId}";
    }

    private static string? TryGetYoutubeVideoId(Uri uri)
    {
        if (!IsHost(uri, "youtube.com"))
            return null;

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var videoId = query["v"];

        return string.IsNullOrWhiteSpace(videoId) || !YoutubeIdRegex().IsMatch(videoId)
            ? null
            : videoId;
    }

    private static string? TryGetYoutubeShortVideoId(Uri uri)
    {
        if (!IsHost(uri, "youtu.be"))
            return null;

        var videoId = uri.AbsolutePath.TrimStart('/');
        return string.IsNullOrWhiteSpace(videoId) || !YoutubeIdRegex().IsMatch(videoId)
            ? null
            : videoId;
    }

    private static string? TryGetVimeoVideoId(Uri uri)
    {
        if (!IsHost(uri, "vimeo.com"))
            return null;

        var videoId = uri.AbsolutePath.TrimStart('/').Split('/')[0];
        return string.IsNullOrWhiteSpace(videoId) || !VimeoIdRegex().IsMatch(videoId)
            ? null
            : videoId;
    }

    private static bool IsHost(Uri uri, string host)
    {
        return uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase);
    }
}

