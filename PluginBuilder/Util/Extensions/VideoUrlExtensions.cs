namespace PluginBuilder.Util.Extensions;

public static class VideoUrlExtensions
{
    private static readonly string[] SupportedVideoHosts =
    [
        "youtube.com",
        "youtu.be",
        "vimeo.com"
    ];

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
        return uri != null && SupportedVideoHosts.Any(host => uri.Host.Contains(host, StringComparison.OrdinalIgnoreCase));
    }

    // converts a video URL to its embeddable format
    public static string? GetVideoEmbedUrl(this string? videoUrl)
    {
        if (string.IsNullOrWhiteSpace(videoUrl))
            return null;

        try
        {
            var uri = new Uri(videoUrl);

            if (uri.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase))
            {
                var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                var videoId = query["v"];
                if (!string.IsNullOrWhiteSpace(videoId))
                {
                    return $"https://www.youtube.com/embed/{videoId}";
                }
            }
            else if (uri.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
            {
                var videoId = uri.AbsolutePath.TrimStart('/');
                if (!string.IsNullOrWhiteSpace(videoId))
                {
                    return $"https://www.youtube.com/embed/{videoId}";
                }
            }
            else if (uri.Host.Contains("vimeo.com", StringComparison.OrdinalIgnoreCase))
            {
                var videoId = uri.AbsolutePath.TrimStart('/');
                if (!string.IsNullOrWhiteSpace(videoId))
                {
                    return $"https://player.vimeo.com/video/{videoId}";
                }
            }
        }
        catch
        {
            return null;
        }
        return null;
    }
}
