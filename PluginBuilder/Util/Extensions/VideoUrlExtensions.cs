using System.Text.RegularExpressions;

namespace PluginBuilder.Util.Extensions;

public enum VideoPlayerKind
{
    /// <summary>Third party player rendered in an iframe (YouTube, Vimeo).</summary>
    Embed,

    /// <summary>Direct video file played by the browser through a &lt;video&gt; element.</summary>
    File
}

/// <summary>How a plugin video should be rendered: <paramref name="Url" /> is the iframe source for
/// <see cref="VideoPlayerKind.Embed" />, or the file to play for <see cref="VideoPlayerKind.File" />.</summary>
public record VideoSource(VideoPlayerKind Kind, string Url);

public static partial class VideoUrlExtensions
{
    [GeneratedRegex(@"^[A-Za-z0-9_-]{11}$")]
    private static partial Regex YoutubeIdRegex();

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex VimeoIdRegex();

    public static bool IsSupportedVideoUrl(this string? videoUrl)
    {
        return videoUrl.GetVideoSource() != null;
    }

    public static VideoSource? GetVideoSource(this string? videoUrl)
    {
        if (!TryParseVideoUri(videoUrl, out var uri)) return null;

        // A link to a platform we embed has to carry a usable video id, otherwise the iframe would be broken
        // and falling back to a media player would not help either.
        if (IsHost(uri!, "youtube.com") || IsHost(uri!, "youtu.be"))
        {
            var youtubeId = TryGetYoutubeVideoId(uri!);
            return youtubeId is null ? null : new VideoSource(VideoPlayerKind.Embed, $"https://www.youtube.com/embed/{youtubeId}");
        }

        if (IsHost(uri!, "vimeo.com"))
        {
            var vimeoId = TryGetVimeoVideoId(uri!);
            return vimeoId is null ? null : new VideoSource(VideoPlayerKind.Embed, $"https://player.vimeo.com/video/{vimeoId}");
        }

        // Anything else is handed to the browser as a media file. Whether it plays is decided by the
        // Content-Type the host returns, which is why no extension is required and why we advertise no type
        // of our own: the URL an author types is not evidence of what the host actually serves.
        return new VideoSource(VideoPlayerKind.File, uri!.AbsoluteUri);
    }

    public static string? GetVideoThumbnailUrl(this string? videoUrl)
    {
        if (!TryParseVideoUri(videoUrl, out var uri)) return null;

        var youtubeId = TryGetYoutubeVideoId(uri!);
        if (!string.IsNullOrEmpty(youtubeId))
            return $"https://i.ytimg.com/vi/{youtubeId}/hqdefault.jpg";

        var vimeoId = TryGetVimeoVideoId(uri!);
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
