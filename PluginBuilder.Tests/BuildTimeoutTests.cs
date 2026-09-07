using System.Diagnostics;
using System.Reflection;
using Dapper;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class BuildTimeoutTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    [Fact]
    public Task BuildTimeoutStartsAfterDockerResourcesAreCreated()
    {
        return AssertDockerResourcesAreCleaned(workerCreateFails: false);
    }

    [Fact]
    public Task WorkerCreateFailureCleansAllSandboxResources()
    {
        return AssertDockerResourcesAreCleaned(workerCreateFails: true);
    }

    private async Task AssertDockerResourcesAreCleaned(bool workerCreateFails)
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create(
            delayWorkerCreate: !workerCreateFails,
            failWorkerCreate: workerCreateFails);
        using var environment = fakeDocker.InstallEnvironment(skipStartupBuild: true);

        await using var tester = Create(workerCreateFails ? "FailingWorkerCreate" : "DelayedWorkerCreate");
        tester.ReuseDatabase = false;
        tester.BuildTimeoutSeconds = 1;
        tester.BuildScratchRoot = fakeDocker.Directory;
        await tester.Start();
        MarkExecutorReady(tester);

        var ownerId = await tester.CreateFakeUserAsync();
        var pluginSlug = new PluginSlug("delayed-" + Guid.NewGuid().ToString("N")[..8]);
        await using var connection = await tester.GetService<DBConnectionFactory>().Open();
        Assert.True(await connection.NewPlugin(pluginSlug, ownerId));
        var buildId = await connection.NewBuild(
            pluginSlug,
            new PluginBuildParameters("https://github.com/example/plugin"));
        var fullBuildId = new FullBuildId(pluginSlug, buildId);

        var stopwatch = Stopwatch.StartNew();
        var exception = await Assert.ThrowsAsync<BuildServiceException>(() =>
            tester.GetService<BuildService>().Build(fullBuildId).WaitAsync(TimeSpan.FromSeconds(15)));
        stopwatch.Stop();

        Assert.Contains(
            workerCreateFails ? "Creating build container" : "timed out",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
        if (!workerCreateFails)
            Assert.True(
                stopwatch.Elapsed >= TimeSpan.FromSeconds(2.5),
                $"Build timeout started before resource preparation completed: {stopwatch.Elapsed}");

        Assert.Empty(Directory.EnumerateDirectories(fakeDocker.Directory, "pb-build-*", SearchOption.AllDirectories));
        var commands = await fakeDocker.ReadCommands();
        Assert.Contains(
            commands,
            command => command.StartsWith("network create ", StringComparison.Ordinal));
        Assert.Contains(
            commands,
            command => command.StartsWith("container create --name pb-proxy-", StringComparison.Ordinal));
        Assert.Contains(
            commands,
            command => command.StartsWith("container create --name pb-worker-", StringComparison.Ordinal));
        Assert.Contains(
            commands,
            command => command.StartsWith("container rm --force pb-worker-", StringComparison.Ordinal));
        Assert.Contains(
            commands,
            command => command.StartsWith("container rm --force pb-proxy-", StringComparison.Ordinal));
        Assert.Equal(
            2,
            commands.Count(command => command.StartsWith("network rm pb-", StringComparison.Ordinal)));

        if (workerCreateFails)
            Assert.DoesNotContain(
                commands,
                command => command.StartsWith("container start --attach pb-worker-", StringComparison.Ordinal));
        else
            Assert.Contains(
                commands,
                command => command.StartsWith("container start --attach pb-worker-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AdmissionAllowsTwoRunningAndTwoQueuedThenRejectsTheFifthBuild()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create();
        using var environment = fakeDocker.InstallEnvironment(skipStartupBuild: true);
        await using var tester = Create("HangingBuilds");
        tester.ReuseDatabase = false;
        tester.BuildTimeoutSeconds = 1;
        tester.BuildScratchRoot = fakeDocker.Directory;
        await tester.Start();
        MarkExecutorReady(tester);

        var ownerId = await tester.CreateFakeUserAsync();
        var pluginSlug = new PluginSlug($"timeout-{Guid.NewGuid():N}"[..16]);
        await using var connection = await tester.GetService<DBConnectionFactory>().Open();
        Assert.True(await connection.NewPlugin(pluginSlug, ownerId));
        List<FullBuildId> admittedBuildIds = [];
        for (var i = 0; i < DockerBuildSandbox.MaxConcurrentBuilds * 2; i++)
        {
            var buildId = await connection.NewBuild(
                pluginSlug,
                new PluginBuildParameters("https://github.com/example/plugin"));
            admittedBuildIds.Add(new FullBuildId(pluginSlug, buildId));
        }
        var rejectedBuildId = new FullBuildId(
            pluginSlug,
            await connection.NewBuild(
                pluginSlug,
                new PluginBuildParameters("https://github.com/example/plugin")));

        var semaphore = (SemaphoreSlim)typeof(BuildService)
            .GetField("_semaphore", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;
        var admission = (SemaphoreSlim)typeof(BuildService)
            .GetField("_admission", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;
        Assert.Equal(DockerBuildSandbox.MaxConcurrentBuilds, semaphore.CurrentCount);
        Assert.Equal(DockerBuildSandbox.MaxConcurrentBuilds * 2, admission.CurrentCount);

        var buildService = tester.GetService<BuildService>();
        var builds = admittedBuildIds.Select(buildService.Build).ToArray();
        await WaitForSemaphoreCount(admission, expected: 0);

        await buildService.Build(rejectedBuildId).WaitAsync(TimeSpan.FromSeconds(10));
        var rejected = await connection.QuerySingleAsync<(string state, string error)>(
            "SELECT state, build_info->>'error' AS error FROM builds WHERE plugin_slug=@pluginSlug AND id=@buildId",
            new { pluginSlug = pluginSlug.ToString(), buildId = rejectedBuildId.BuildId });
        Assert.Equal(BuildStates.Failed.ToEventName(), rejected.state);
        Assert.Equal("The isolated build queue is full. Please try again later.", rejected.error);

        foreach (var build in builds)
        {
            var exception = await Assert.ThrowsAsync<BuildServiceException>(async () =>
                await build.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Contains("timed out", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(DockerBuildSandbox.MaxConcurrentBuilds, semaphore.CurrentCount);
        Assert.Equal(DockerBuildSandbox.MaxConcurrentBuilds * 2, admission.CurrentCount);
        foreach (var buildId in admittedBuildIds)
        {
            var row = await connection.QuerySingleAsync<(string state, string error)>(
                "SELECT state, build_info->>'error' AS error FROM builds WHERE plugin_slug=@pluginSlug AND id=@buildId",
                new { pluginSlug = pluginSlug.ToString(), buildId = buildId.BuildId });
            Assert.Equal(BuildStates.Failed.ToEventName(), row.state);
            Assert.Contains("timed out", row.error, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Empty(Directory.EnumerateDirectories(fakeDocker.Directory, "pb-build-*", SearchOption.AllDirectories));
        var commands = await fakeDocker.ReadCommands();
        Assert.Equal(
            admittedBuildIds.Count,
            commands.Count(command => command.StartsWith("container create --name pb-worker-", StringComparison.Ordinal)));
        Assert.Equal(
            admittedBuildIds.Count,
            commands.Count(command => command.StartsWith("container rm --force pb-worker-", StringComparison.Ordinal)));
        Assert.Equal(
            admittedBuildIds.Count,
            commands.Count(command => command.StartsWith("container rm --force pb-proxy-", StringComparison.Ordinal)));
        Assert.Equal(
            admittedBuildIds.Count * 2,
            commands.Count(command => command.StartsWith("network rm pb-", StringComparison.Ordinal)));
    }

    private static void MarkExecutorReady(ServerTester tester)
    {
        tester.GetService<BuildExecutorState>().MarkReady(
            "sha256:1111111111111111111111111111111111111111111111111111111111111111",
            "sha256:2222222222222222222222222222222222222222222222222222222222222222");
    }

    private static async Task WaitForSemaphoreCount(SemaphoreSlim semaphore, int expected)
    {
        var timeout = Stopwatch.StartNew();
        while (semaphore.CurrentCount != expected && timeout.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(10);

        Assert.Equal(expected, semaphore.CurrentCount);
    }

    private sealed class FakeDocker : IAsyncDisposable
    {
        private readonly bool _delayWorkerCreate;
        private readonly bool _failWorkerCreate;

        private FakeDocker(string directory, bool delayWorkerCreate, bool failWorkerCreate)
        {
            Directory = directory;
            _delayWorkerCreate = delayWorkerCreate;
            _failWorkerCreate = failWorkerCreate;
        }

        public string Directory { get; }

        public static async Task<FakeDocker> Create(
            bool delayWorkerCreate = false,
            bool failWorkerCreate = false)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"plugin-builder-timeout-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(directory);
            for (var slot = 0; slot < DockerBuildSandbox.MaxConcurrentBuilds; slot++)
                System.IO.Directory.CreateDirectory(DockerBuildSandbox.ScratchSlotPath(directory, slot));
            var dockerPath = Path.Combine(directory, "docker");
            await File.WriteAllTextAsync(dockerPath, """
                #!/bin/sh
                set -eu
                state="${PB_FAKE_DOCKER_STATE:?}"
                printf '%s\n' "$*" >> "$state/commands"

                case "$1:$2" in
                    network:create|network:connect|network:rm|container:exec)
                        ;;
                    container:create)
                        previous=""
                        name=""
                        for argument in "$@"; do
                            if [ "$previous" = "--name" ]; then name="$argument"; break; fi
                            previous="$argument"
                        done
                        [ -n "$name" ]
                        case "$name" in
                            pb-worker-*)
                                if [ "${PB_FAKE_WORKER_CREATE_FAIL:-false}" = "true" ]; then
                                    printf '%s\n' 'simulated worker create failure' >&2
                                    exit 21
                                fi
                                if [ "${PB_FAKE_WORKER_CREATE_DELAY:-false}" = "true" ]; then
                                    sleep 2
                                fi
                                ;;
                        esac
                        ;;
                    container:start)
                        target=""
                        for argument in "$@"; do target="$argument"; done
                        case "$target" in pb-worker-*) sleep 30 ;; esac
                        ;;
                    container:inspect)
                        case "$*" in
                            *State.Running*) printf '%s\n' true ;;
                            *IPAddress*) printf '%s\n' 172.31.0.2 ;;
                            *) exit 2 ;;
                        esac
                        ;;
                    container:rm)
                        ;;
                    *)
                        exit 2
                        ;;
                esac
                """);
            File.SetUnixFileMode(
                dockerPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return new FakeDocker(directory, delayWorkerCreate, failWorkerCreate);
        }

        public IDisposable InstallEnvironment(bool skipStartupBuild)
        {
            var values = new Dictionary<string, string?>
            {
                ["PATH"] = Directory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
                ["DOCKER_STARTUP_SKIP_BUILD"] = skipStartupBuild ? "true" : null,
                ["PB_FAKE_DOCKER_STATE"] = Directory,
                ["PB_FAKE_WORKER_CREATE_DELAY"] = _delayWorkerCreate ? "true" : "false",
                ["PB_FAKE_WORKER_CREATE_FAIL"] = _failWorkerCreate ? "true" : "false"
            };
            return new EnvironmentScope(values);
        }

        public async Task<List<string>> ReadCommands()
        {
            var path = Path.Combine(Directory, "commands");
            return File.Exists(path)
                ? (await File.ReadAllLinesAsync(path)).ToList()
                : [];
        }

        public ValueTask DisposeAsync()
        {
            System.IO.Directory.Delete(Directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> _original;

        public EnvironmentScope(IReadOnlyDictionary<string, string?> values)
        {
            _original = values.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
            foreach (var (key, value) in values)
                Environment.SetEnvironmentVariable(key, value);
        }

        public void Dispose()
        {
            foreach (var (key, value) in _original)
                Environment.SetEnvironmentVariable(key, value);
        }
    }
}
