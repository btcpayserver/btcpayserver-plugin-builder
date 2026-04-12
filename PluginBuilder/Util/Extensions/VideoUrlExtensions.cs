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
        if (!TryParseVideoUri(videoUrl, out var uri)) return null;

        var youtubeId = TryGetYoutubeVideoId(uri!);
        if (!string.IsNullOrEmpty(youtubeId))
            return $"https://www.youtube.com/embed/{youtubeId}";

        var vimeoId = TryGetVimeoVideoId(uri!);
        return !string.IsNullOrEmpty(vimeoId) ? $"https://player.vimeo.com/video/{vimeoId}" : null;
    }

    public static string? GetVideoThumbnailUrl(this string? videoUrl)
    {
        if (!TryParseVideoUri(videoUrl, out var uri)) return null;

        var youtubeId = TryGetYoutubeVideoId(uri);
        if (!string.IsNullOrEmpty(youtubeId))
            return $"https://i.ytimg.com/vi/{youtubeId}/hqdefault.jpg";

        var vimeoId = TryGetVimeoVideoId(uri);
        if (!string.IsNullOrEmpty(vimeoId))
            return $"https://vumbnail.com/{vimeoId}.jpg";

        return null;
    }

    private static string? TryGetYoutubeVideoId(Uri uri)
    {
        if (IsHost(uri, "youtube.com"))
        {
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var videoId = query["v"];
            return string.IsNullOrWhiteSpace(videoId) || !YoutubeIdRegex().IsMatch(videoId)
                ? null
                : videoId;
        }

        if (IsHost(uri, "youtu.be"))
        {
            var videoId = uri.AbsolutePath.TrimStart('/');
            return string.IsNullOrWhiteSpace(videoId) || !YoutubeIdRegex().IsMatch(videoId)
                ? null
                : videoId;
        }

        return null;
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

    private static bool TryParseVideoUri(string? videoUrl, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(videoUrl)) return false;
        if (!Uri.TryCreate(videoUrl, UriKind.Absolute, out uri)) return false;
        return uri.Scheme == Uri.UriSchemeHttps;
    }

    private static bool IsHost(Uri uri, string host)
    {
        return uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase);
    }
}

