using Xunit;

using PluginBuilder.Builds.Services;

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

    [Fact]
    public void AdmissionCanBeSuspendedAndResumedWithoutReplacingTheGeneration()
    {
        var state = new BuildExecutorState();
        state.MarkReady("sha256:worker", "sha256:proxy");
        var token = state.StopToken;

        state.SuspendAdmission("Health probe failed");
        Assert.False(state.Snapshot.IsReady);
        Assert.Equal("Health probe failed", state.Snapshot.UnavailableReason);
        Assert.Equal("sha256:worker", state.Snapshot.WorkerImageId);
        Assert.Equal("sha256:proxy", state.Snapshot.ProxyImageId);
        Assert.Equal(token, state.StopToken);
        Assert.False(token.IsCancellationRequested);

        state.ResumeAdmission("sha256:worker", "sha256:proxy");
        Assert.True(state.Snapshot.IsReady);
        Assert.Null(state.Snapshot.UnavailableReason);
        Assert.Equal(token, state.StopToken);
        Assert.False(token.IsCancellationRequested);
    }

    [Fact]
    public void GuardedAdmissionTransitionsPreserveImagesAndRejectStoppedOrReplacedGenerations()
    {
        var state = new BuildExecutorState();
        var startup = state.StopToken;
        Assert.False(state.TrySuspendAdmission(startup, "Timeout"));
        Assert.False(state.TryResumeAdmission(startup));
        state.MarkReady("sha256:worker", "sha256:proxy");
        var generation = state.StopToken;
        var ready = state.Snapshot;
        Assert.True(state.TrySuspendAdmission(generation, "Timeout"));
        Assert.False(state.TrySuspendAdmission(generation, "Another timeout"));
        Assert.True(state.TryResumeAdmission(generation));
        Assert.Equal(ready, state.Snapshot);
        Assert.Equal(generation, state.StopToken);

        state.MarkUnavailable("Cleanup failed");
        var stopped = state.Snapshot;
        Assert.False(state.TrySuspendAdmission(generation, "Timeout"));
        Assert.False(state.TryResumeAdmission(generation));
        Assert.Equal(stopped, state.Snapshot);
        state.MarkReady("sha256:new-worker", "sha256:new-proxy");
        var replacement = state.Snapshot;
        Assert.False(state.TrySuspendAdmission(generation, "Timeout"));
        Assert.False(state.TryResumeAdmission(generation));
        Assert.Equal(replacement, state.Snapshot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResumeAdmissionCannotStartOrResurrectAGeneration(bool previouslyReady)
    {
        var state = new BuildExecutorState();
        if (previouslyReady)
        {
            state.MarkReady("sha256:worker", "sha256:proxy");
            state.MarkUnavailable("Shutdown");
        }
        var token = state.StopToken;
        Assert.Throws<InvalidOperationException>(() => state.ResumeAdmission("sha256:worker", "sha256:proxy"));
        Assert.False(state.Snapshot.IsReady);
        Assert.Equal(token, state.StopToken);
        Assert.True(token.IsCancellationRequested);
    }
}
