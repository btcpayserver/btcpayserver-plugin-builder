using PluginBuilder.Services;
using Xunit;

namespace PluginBuilder.Tests;

public class BuildExecutorStateTests
{
    [Fact]
    public void ExecutorStartsUnavailableWithoutImages()
    {
        var state = new BuildExecutorState();

        var snapshot = state.Snapshot;

        Assert.False(snapshot.IsReady);
        Assert.Null(snapshot.WorkerImageId);
        Assert.Null(snapshot.ProxyImageId);
    }

    [Fact]
    public void MarkReadyPublishesBothImmutableImageIdsAtomically()
    {
        var state = new BuildExecutorState();

        state.MarkReady("sha256:worker", "sha256:proxy");

        Assert.Equal(
            new BuildExecutorSnapshot(
                IsReady: true,
                WorkerImageId: "sha256:worker",
                ProxyImageId: "sha256:proxy",
                UnavailableReason: null),
            state.Snapshot);
    }

    [Fact]
    public void MarkUnavailableClearsPreviouslyTrustedImageIds()
    {
        var state = new BuildExecutorState();
        state.MarkReady("sha256:worker", "sha256:proxy");

        state.MarkUnavailable("sandbox cleanup failed");

        Assert.Equal(
            new BuildExecutorSnapshot(
                IsReady: false,
                WorkerImageId: null,
                ProxyImageId: null,
                UnavailableReason: "sandbox cleanup failed"),
            state.Snapshot);
    }

    [Fact]
    public void MarkUnavailableCancelsCurrentStopToken()
    {
        var state = new BuildExecutorState();
        state.MarkReady("sha256:worker", "sha256:proxy");
        var activeToken = state.StopToken;
        Assert.False(activeToken.IsCancellationRequested);

        state.MarkUnavailable("shutdown");

        Assert.True(activeToken.IsCancellationRequested);
        Assert.True(state.StopToken.IsCancellationRequested);
    }

    [Fact]
    public void MarkReadyReplacesCancelledStopTokenWithFreshToken()
    {
        var state = new BuildExecutorState();
        var startupToken = state.StopToken;
        Assert.True(startupToken.IsCancellationRequested);

        state.MarkReady("sha256:worker-one", "sha256:proxy-one");
        var firstReadyToken = state.StopToken;
        Assert.NotEqual(startupToken, firstReadyToken);
        Assert.False(firstReadyToken.IsCancellationRequested);

        state.MarkUnavailable("restart");
        Assert.True(firstReadyToken.IsCancellationRequested);
        state.MarkReady("sha256:worker-two", "sha256:proxy-two");

        var secondReadyToken = state.StopToken;
        Assert.NotEqual(firstReadyToken, secondReadyToken);
        Assert.False(secondReadyToken.IsCancellationRequested);
    }

    [Theory]
    [InlineData(null, "sha256:proxy")]
    [InlineData("", "sha256:proxy")]
    [InlineData("sha256:worker", null)]
    [InlineData("sha256:worker", " ")]
    public void MarkReadyRejectsMissingImageIds(string? workerImageId, string? proxyImageId)
    {
        var state = new BuildExecutorState();

        Assert.ThrowsAny<ArgumentException>(() => state.MarkReady(workerImageId!, proxyImageId!));
        Assert.False(state.Snapshot.IsReady);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void MarkUnavailableRequiresAnActionableReason(string? reason)
    {
        var state = new BuildExecutorState();
        state.MarkReady("sha256:worker", "sha256:proxy");

        Assert.ThrowsAny<ArgumentException>(() => state.MarkUnavailable(reason!));
        Assert.True(state.Snapshot.IsReady);
    }
}
