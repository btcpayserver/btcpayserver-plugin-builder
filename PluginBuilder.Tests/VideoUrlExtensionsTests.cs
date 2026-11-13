using PluginBuilder.Util.Extensions;
using Xunit;

namespace PluginBuilder.Tests;

public class VideoUrlExtensionsTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "https://www.youtube.com/embed/dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", "https://www.youtube.com/embed/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?v=9bZkp7q19f0&t=30s", "https://www.youtube.com/embed/9bZkp7q19f0")]
    [InlineData("https://vimeo.com/123456789", "https://player.vimeo.com/video/123456789")]
    public void PlatformUrlsAreEmbedded(string videoUrl, string expectedUrl)
    {
        var source = videoUrl.GetVideoSource();
        Assert.NotNull(source);
        Assert.Equal(VideoPlayerKind.Embed, source.Kind);
        Assert.Equal(expectedUrl, source.Url);
        Assert.NotNull(videoUrl.GetVideoThumbnailUrl());
    }

    // The URL is passed through untouched whatever it looks like: only the Content-Type the host returns
    // decides whether the browser plays it, so the extension is never used to claim a type.
    [Theory]
    [InlineData("https://cdn.example.com/9f86d081884c7d659a2feaa0c55ad015.mp4")]
    [InlineData("https://example.com/media/demo.MP4")]
    [InlineData("https://example.com/media/demo.mp4?token=abc")]
    [InlineData("https://cdn.example.com/9f86d081884c7d659a2feaa0c55ad015")]
    [InlineData("https://example.com/media/demo.webm")]
    [InlineData("https://www.dailymotion.com/video/x8abc123")]
    public void EverythingElseIsPlayedAsAFile(string videoUrl)
    {
        var source = videoUrl.GetVideoSource();
        Assert.NotNull(source);
        Assert.Equal(VideoPlayerKind.File, source.Kind);
        Assert.Equal(videoUrl, source.Url);
        Assert.Null(videoUrl.GetVideoThumbnailUrl());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-valid-url")]
    [InlineData("http://example.com/demo.mp4")] // https only
    [InlineData("javascript:alert(1)")]
    // Platform links still need a usable video id, an iframe pointing at nothing helps nobody
    [InlineData("https://www.youtube.com/watch?v=tooshort")]
    [InlineData("https://www.youtube.com/feed/subscriptions")]
    [InlineData("https://youtu.be/")]
    [InlineData("https://vimeo.com/channels/staffpicks")]
    public void UnsupportedUrlsAreRejected(string? videoUrl)
    {
        Assert.Null(videoUrl.GetVideoSource());
        Assert.False(videoUrl.IsSupportedVideoUrl());
    }
}
