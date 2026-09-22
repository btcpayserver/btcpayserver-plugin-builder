using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using PluginBuilder.BuildBroker;
using Xunit;

using PluginBuilder.Builds.Services;

namespace PluginBuilder.Tests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class BrokerDockerMonitorTests
{
    [UnixFact]
    public async Task SuccessfulFixedProbePreservesReadinessAndTheActiveBuildCancellationToken()
    {
        using var fixture = new Fixture();
        var activeBuildToken = fixture.State.StopToken;
        await fixture.Monitor.CheckOnceAsync();
        await fixture.Monitor.CheckOnceAsync();
        Assert.True(fixture.State.Snapshot.IsReady);
        Assert.False(activeBuildToken.IsCancellationRequested);
        Assert.Equal(activeBuildToken, fixture.State.StopToken);
        Assert.Equal(["version --format {{.Server.Version}}", "version --format {{.Server.Version}}"], fixture.Commands());
    }

    [UnixFact]
    public async Task UnavailableExecutorDoesNotStartAnyDockerProcess()
    {
        using var fixture = new Fixture();
        fixture.State.MarkUnavailable("Startup failed.");
        await fixture.Monitor.CheckOnceAsync();
        Assert.Empty(fixture.Commands());
        Assert.Equal("Startup failed.", fixture.State.Snapshot.UnavailableReason);
    }

    [UnixFact]
    public async Task NonzeroExitCancelsBuildsAndNeverAutomaticallyReadmitsAfterDockerRecovers()
    {
        using var fixture = new Fixture("failure");
        var activeBuildToken = fixture.State.StopToken;
        await fixture.Monitor.CheckOnceAsync();
        Assert.False(fixture.State.Snapshot.IsReady);
        Assert.True(activeBuildToken.IsCancellationRequested);
        fixture.SetMode("success");
        await fixture.Monitor.CheckOnceAsync();
        Assert.False(fixture.State.Snapshot.IsReady);
        Assert.Single(fixture.Commands());
    }

    [UnixFact]
    public async Task MissingDockerExecutableFailsClosedWithoutAttemptingAnyFallback()
    {
        using var fixture = new Fixture();
        File.Move(fixture.DockerPath, fixture.DockerPath + ".disabled");
        var activeBuildToken = fixture.State.StopToken;
        await fixture.Monitor.CheckOnceAsync();
        Assert.False(fixture.State.Snapshot.IsReady);
        Assert.True(activeBuildToken.IsCancellationRequested);
        Assert.Empty(fixture.Commands());
    }

    [UnixTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThreeConsecutiveTenSecondTimeoutsDisableAdmissionAndSuccessResetsTheCount(bool recoverBeforeLimit)
    {
        using var fixture = new Fixture("hang");
        var activeBuildToken = fixture.State.StopToken;
        var expectedCommands = 0;
        if (recoverBeforeLimit)
        {
            await AssertTimedOutProbe();
            await AssertTimedOutProbe();
            fixture.SetMode("success");
            await fixture.Monitor.CheckOnceAsync();
            expectedCommands++;
            Assert.True(fixture.State.Snapshot.IsReady);
            Assert.Equal(activeBuildToken, fixture.State.StopToken);
            Assert.False(activeBuildToken.IsCancellationRequested);
            fixture.SetMode("hang");
        }

        await AssertTimedOutProbe();
        await AssertTimedOutProbe();
        await AssertTimedOutProbe(expectReady: false);
        Assert.False(fixture.State.Snapshot.IsReady);
        Assert.True(activeBuildToken.IsCancellationRequested);
        Assert.Contains("timed out", fixture.State.Snapshot.UnavailableReason, StringComparison.Ordinal);
        fixture.SetMode("success");
        await fixture.Monitor.CheckOnceAsync();
        Assert.False(fixture.State.Snapshot.IsReady);
        Assert.Equal(expectedCommands, fixture.Commands().Length);

        async Task AssertTimedOutProbe(bool expectReady = true)
        {
            var watch = Stopwatch.StartNew();
            await fixture.Monitor.CheckOnceAsync().WaitAsync(TimeSpan.FromSeconds(16));
            Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(16));
            expectedCommands++;
            Assert.Equal(expectReady, fixture.State.Snapshot.IsReady);
            Assert.Equal(!expectReady, activeBuildToken.IsCancellationRequested);
            Assert.Equal(activeBuildToken, fixture.State.StopToken);
        }
    }

    [UnixFact]
    public async Task ConcurrentChecksDoNotFanOutIntoMultipleDockerProcesses()
    {
        using var fixture = new Fixture("blocked");
        var firstProbe = fixture.Monitor.CheckOnceAsync();
        Task otherProbes = Task.CompletedTask;
        try
        {
            await fixture.WaitForProbeStart();
            otherProbes = Task.WhenAll(Enumerable.Range(0, 19).Select(_ => fixture.Monitor.CheckOnceAsync()));
            await otherProbes.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(firstProbe.IsCompleted);
            Assert.Single(fixture.Commands());
        }
        finally
        {
            fixture.ReleaseProbe();
            await Task.WhenAll(firstProbe, otherProbes).WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.True(fixture.State.Snapshot.IsReady);
        Assert.Single(fixture.Commands());
    }

    [UnixFact]
    public async Task HostedShutdownCancellationDoesNotMisdiagnoseADaemonFailure()
    {
        using var fixture = new Fixture("blocked");
        using var shutdown = new CancellationTokenSource();
        var probe = fixture.Monitor.CheckOnceAsync(shutdown.Token);
        try
        {
            await fixture.WaitForProbeStart();
            shutdown.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            shutdown.Cancel();
            fixture.ReleaseProbe();
            try { await probe.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        }
        Assert.True(fixture.State.Snapshot.IsReady);
        Assert.Single(fixture.Commands());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string? _previousPath;
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "pb-docker-monitor-test-" + Guid.NewGuid().ToString("N"));
        public string DockerPath => Path.Combine(_directory, "docker");
        public BuildExecutorState State { get; } = new();
        public BrokerDockerMonitor Monitor { get; }

        public Fixture(string mode = "success")
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(DockerPath, """
                #!/bin/sh
                directory=${0%/*}
                printf '%s\n' "$*" >> "$directory/commands"
                IFS= read -r mode < "$directory/mode"
                case "$mode" in
                  success) exit 0 ;;
                  failure) exit 7 ;;
                  hang) exec /bin/sleep 30 ;;
                  blocked)
                    : > "$directory/started"
                    while [ ! -f "$directory/release" ]; do /bin/sleep 0.01; done
                    exit 0 ;;
                  *) exit 99 ;;
                esac
                """);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(DockerPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            SetMode(mode);
            _previousPath = Environment.GetEnvironmentVariable("PATH");
            // No fallback to a host Docker binary, even in the missing-command test.
            Environment.SetEnvironmentVariable("PATH", _directory);
            State.MarkReady("sha256:" + new string('a', 64), "sha256:" + new string('b', 64));
            Monitor = new BrokerDockerMonitor(new ProcessRunner(NullLogger<ProcessRunner>.Instance), State,
                NullLogger<BrokerDockerMonitor>.Instance);
        }

        public void SetMode(string mode) => File.WriteAllText(Path.Combine(_directory, "mode"), mode + "\n");
        public void ReleaseProbe() => File.WriteAllText(Path.Combine(_directory, "release"), "");

        public async Task WaitForProbeStart()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!File.Exists(Path.Combine(_directory, "started")))
                await Task.Delay(10, deadline.Token);
        }

        public string[] Commands() => File.Exists(Path.Combine(_directory, "commands"))
            ? File.ReadAllLines(Path.Combine(_directory, "commands")) : [];

        public void Dispose()
        {
            Monitor.Dispose();
            Environment.SetEnvironmentVariable("PATH", _previousPath);
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }
}
