using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using PluginBuilder.BuildBroker;
using PluginBuilder.BuildBroker.Configuration;
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
    public async Task ThreeConsecutiveTimeoutsSuspendAdmissionAndSuccessResetsTheCount()
    {
        var probeTimeout = TimeSpan.FromSeconds(1);
        using var fixture = new Fixture("hang", probeTimeout);
        var activeBuildToken = fixture.State.StopToken;
        // Recover before the threshold, after suspension, and once more afterwards.
        int[] timeoutCounts = [2, 4, 1];
        foreach (var timeoutCount in timeoutCounts)
        {
            fixture.SetMode("hang");
            for (var attempt = 1; attempt <= timeoutCount; attempt++)
            {
                var watch = Stopwatch.StartNew();
                await fixture.Monitor.CheckOnceAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.InRange(watch.Elapsed, probeTimeout * 0.9, TimeSpan.FromSeconds(10));
                Assert.Equal(attempt < 3, fixture.State.Snapshot.IsReady);
                Assert.False(activeBuildToken.IsCancellationRequested);
                Assert.Equal(activeBuildToken, fixture.State.StopToken);
            }
            fixture.SetMode("success");
            await fixture.Monitor.CheckOnceAsync();
            Assert.True(fixture.State.Snapshot.IsReady);
            Assert.Equal(activeBuildToken, fixture.State.StopToken);
            Assert.False(activeBuildToken.IsCancellationRequested);
        }
        Assert.Equal(timeoutCounts.Sum() + timeoutCounts.Length, fixture.Commands().Length);
    }

    [UnixTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task HardFailureCancelsBuildsWithoutAutomaticReadmission(bool suspended, bool missingExecutable)
    {
        using var fixture = new Fixture("hang", TimeSpan.FromSeconds(1));
        if (suspended)
            for (var i = 0; i < 3; i++) await fixture.Monitor.CheckOnceAsync();
        var token = fixture.State.StopToken;
        Assert.False(token.IsCancellationRequested);
        if (missingExecutable) File.Move(fixture.DockerPath, fixture.DockerPath + ".disabled");
        else fixture.SetMode("failure");
        await fixture.Monitor.CheckOnceAsync();
        Assert.True(token.IsCancellationRequested);
        Assert.False(fixture.State.Snapshot.IsReady);
        var commands = fixture.Commands().Length;
        Assert.Equal((suspended ? 3 : 0) + (missingExecutable ? 0 : 1), commands);
        fixture.SetMode("success");
        await fixture.Monitor.CheckOnceAsync();
        Assert.Equal(commands, fixture.Commands().Length);
    }

    [UnixTheory]
    [InlineData("cleanup")]
    [InlineData("generation")]
    [InlineData("shutdown")]
    public async Task LateSuccessCannotResumeAdmissionAfterStopOrGenerationChange(string cause)
    {
        using var fixture = new Fixture("hang", TimeSpan.FromSeconds(1));
        for (var i = 0; i < 3; i++) await fixture.Monitor.CheckOnceAsync();
        fixture.SetMode("blocked");
        var probe = fixture.Monitor.CheckOnceAsync();
        try
        {
            await fixture.WaitForProbeStart();
            if (cause == "shutdown") await fixture.Monitor.StopAsync(CancellationToken.None);
            else
            {
                fixture.State.MarkUnavailable("Cleanup failed");
                if (cause == "generation")
                {
                    fixture.State.MarkReady("sha256:new-worker", "sha256:new-proxy");
                    fixture.State.SuspendAdmission("New generation suspended");
                }
            }
            var expected = fixture.State.Snapshot;
            fixture.ReleaseProbe();
            await probe.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(expected, fixture.State.Snapshot);
            Assert.False(fixture.State.Snapshot.IsReady);
        }
        finally
        {
            fixture.ReleaseProbe();
            await probe.WaitAsync(TimeSpan.FromSeconds(5));
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

        public Fixture(string mode = "success", TimeSpan? probeTimeout = null)
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
            var options = probeTimeout is { } timeout
                ? new BuildExecutorOptions { DockerProbeTimeout = timeout }
                : new BuildExecutorOptions();
            Monitor = new BrokerDockerMonitor(new ProcessRunner(), State, options,
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
