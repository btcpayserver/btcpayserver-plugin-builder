using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using PluginBuilder.Configuration;
using PluginBuilder.Services;
using PluginBuilder.Util;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class DockerBuildSandboxLifecycleTests
{
    private readonly XUnitLogger _log;

    public DockerBuildSandboxLifecycleTests(ITestOutputHelper output) => _log = new XUnitLogger(output);

    private const string WorkerImageId =
        "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string ProxyImageId =
        "sha256:2222222222222222222222222222222222222222222222222222222222222222";

    [Fact]
    public async Task PrepareCreatesPerBuildProxyTopologyAndDisposeCleansEveryResource()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create();
        var state = ReadyExecutor();
        var sandbox = CreateSandbox(fakeDocker, state);
        var prepared = await sandbox.PrepareAsync(BuildId(), BuildInfo());

        Assert.StartsWith("pb-worker-", prepared.WorkerContainer, StringComparison.Ordinal);
        Assert.StartsWith("pb-clone-", prepared.CloneContainer, StringComparison.Ordinal);
        Assert.StartsWith("pb-proxy-", prepared.ProxyContainer, StringComparison.Ordinal);
        Assert.StartsWith("pb-internal-", prepared.InternalNetwork, StringComparison.Ordinal);
        Assert.StartsWith("pb-egress-", prepared.EgressNetwork, StringComparison.Ordinal);
        Assert.NotEqual(prepared.InternalNetwork, prepared.EgressNetwork);

        var commands = await fakeDocker.ReadCommands();
        var egressCreate = Assert.Single(
            commands,
            command => command.StartsWith("network create ", StringComparison.Ordinal) &&
                       command.EndsWith(prepared.EgressNetwork, StringComparison.Ordinal));
        Assert.DoesNotContain("--internal", egressCreate, StringComparison.Ordinal);
        Assert.Contains("--ipv6=false", egressCreate, StringComparison.Ordinal);

        var internalCreate = Assert.Single(
            commands,
            command => command.StartsWith("network create ", StringComparison.Ordinal) &&
                       command.EndsWith(prepared.InternalNetwork, StringComparison.Ordinal));
        Assert.Contains("--internal", internalCreate, StringComparison.Ordinal);
        Assert.Contains("--ipv6=false", internalCreate, StringComparison.Ordinal);
        Assert.Contains(
            "com.docker.network.bridge.inhibit_ipv4=true",
            internalCreate,
            StringComparison.Ordinal);

        var proxyCreate = Assert.Single(
            commands,
            command => command.StartsWith(
                $"container create --name {prepared.ProxyContainer} ",
                StringComparison.Ordinal));
        var proxyResolverFile = Path.Combine(prepared.WorkVolume, ".proxy-resolv.conf");
        Assert.Contains($"--network {prepared.EgressNetwork}", proxyCreate, StringComparison.Ordinal);
        Assert.Contains(
            $"type=bind,source={proxyResolverFile},target=/etc/resolv.conf,readonly",
            proxyCreate,
            StringComparison.Ordinal);
        Assert.DoesNotContain("--dns", proxyCreate, StringComparison.Ordinal);
        Assert.Contains("--runtime runsc", proxyCreate, StringComparison.Ordinal);
        Assert.DoesNotContain(prepared.InternalNetwork, proxyCreate, StringComparison.Ordinal);
        Assert.Contains("--read-only", proxyCreate, StringComparison.Ordinal);
        Assert.Contains("--user 13:13", proxyCreate, StringComparison.Ordinal);
        Assert.Contains("--cap-drop ALL", proxyCreate, StringComparison.Ordinal);
        Assert.Contains("--security-opt no-new-privileges:true", proxyCreate, StringComparison.Ordinal);
        Assert.Contains("--memory 256m --memory-swap 256m", proxyCreate, StringComparison.Ordinal);
        Assert.Contains("--pids-limit 128", proxyCreate, StringComparison.Ordinal);
        Assert.Contains("--ulimit nofile=1024:1024", proxyCreate, StringComparison.Ordinal);
        Assert.DoesNotContain("--publish", proxyCreate, StringComparison.Ordinal);
        Assert.DoesNotContain("--env", proxyCreate, StringComparison.Ordinal);
        Assert.EndsWith(ProxyImageId, proxyCreate, StringComparison.Ordinal);
        Assert.Equal(
            "nameserver 1.1.1.1\nnameserver 1.0.0.1\noptions timeout:1 attempts:2\n",
            await fakeDocker.ReadProxyResolverConfiguration());
        Assert.False(File.Exists(proxyResolverFile));

        var connect = $"network connect {prepared.InternalNetwork} {prepared.ProxyContainer}";
        Assert.Contains(connect, commands);
        var readiness = Assert.Single(
            commands,
            command => command.StartsWith(
                $"container exec {prepared.ProxyContainer} /bin/bash -c ",
                StringComparison.Ordinal));
        Assert.Contains("/dev/tcp/127.0.0.1/3128", readiness, StringComparison.Ordinal);

        var cloneCreate = Assert.Single(
            commands,
            command => command.StartsWith(
                $"container create --name {prepared.CloneContainer} ",
                StringComparison.Ordinal));
        Assert.Contains($"--network {prepared.InternalNetwork}", cloneCreate, StringComparison.Ordinal);
        Assert.DoesNotContain(prepared.EgressNetwork, cloneCreate, StringComparison.Ordinal);
        Assert.Contains(
            "--dns 192.0.2.1 --dns-option timeout:1 --dns-option attempts:1",
            cloneCreate,
            StringComparison.Ordinal);
        Assert.DoesNotContain("--dns 1.1.1.1", cloneCreate, StringComparison.Ordinal);
        Assert.DoesNotContain("--dns 1.0.0.1", cloneCreate, StringComparison.Ordinal);
        Assert.Contains("--runtime runsc", cloneCreate, StringComparison.Ordinal);
        Assert.Contains("--read-only", cloneCreate, StringComparison.Ordinal);
        Assert.Contains("--user 10002:10002", cloneCreate, StringComparison.Ordinal);
        Assert.Contains("--cap-drop ALL", cloneCreate, StringComparison.Ordinal);
        Assert.Contains("--security-opt no-new-privileges:true", cloneCreate, StringComparison.Ordinal);
        Assert.Contains("--memory 1g --memory-swap 1g", cloneCreate, StringComparison.Ordinal);
        Assert.Contains("--pids-limit 128", cloneCreate, StringComparison.Ordinal);
        Assert.Contains("--ulimit nofile=1024:1024", cloneCreate, StringComparison.Ordinal);
        Assert.Contains(
            $"type=bind,source={prepared.SourceVolume},target=/source",
            cloneCreate,
            StringComparison.Ordinal);
        Assert.Contains("HTTP_PROXY=http://172.31.0.2:3128", cloneCreate, StringComparison.Ordinal);
        Assert.Contains("GIT_REPO=https://gitlab.com/example/plugin", cloneCreate, StringComparison.Ordinal);
        Assert.Contains("GIT_REF=main", cloneCreate, StringComparison.Ordinal);
        Assert.Contains("--entrypoint /clone-source.sh", cloneCreate, StringComparison.Ordinal);
        Assert.DoesNotContain("TOKEN=", cloneCreate, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PASSWORD=", cloneCreate, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(WorkerImageId, cloneCreate, StringComparison.Ordinal);

        var cloneStart = $"container start --attach {prepared.CloneContainer}";
        var cloneRemove = $"container rm --force {prepared.CloneContainer}";
        Assert.Contains(cloneStart, commands);
        Assert.Contains(cloneRemove, commands);

        var workerCreate = Assert.Single(
            commands,
            command => command.StartsWith(
                $"container create --name {prepared.WorkerContainer} ",
                StringComparison.Ordinal));
        Assert.Contains($"--network {prepared.InternalNetwork}", workerCreate, StringComparison.Ordinal);
        Assert.DoesNotContain(prepared.EgressNetwork, workerCreate, StringComparison.Ordinal);
        Assert.Contains(
            "--dns 192.0.2.1 --dns-option timeout:1 --dns-option attempts:1",
            workerCreate,
            StringComparison.Ordinal);
        Assert.DoesNotContain("--dns 1.1.1.1", workerCreate, StringComparison.Ordinal);
        Assert.DoesNotContain("--dns 1.0.0.1", workerCreate, StringComparison.Ordinal);
        Assert.Contains("HTTP_PROXY=http://172.31.0.2:3128", workerCreate, StringComparison.Ordinal);
        Assert.Contains(
            $"type=bind,source={prepared.SourceVolume},target=/source,readonly",
            workerCreate,
            StringComparison.Ordinal);
        Assert.DoesNotContain("GIT_REPO=", workerCreate, StringComparison.Ordinal);
        Assert.DoesNotContain("GIT_REF=", workerCreate, StringComparison.Ordinal);

        Assert.True(commands.IndexOf(proxyCreate) < commands.IndexOf(connect));
        Assert.True(commands.IndexOf(connect) < commands.IndexOf(readiness));
        Assert.True(commands.IndexOf(readiness) < commands.IndexOf(cloneCreate));
        Assert.True(commands.IndexOf(cloneCreate) < commands.IndexOf(cloneStart));
        Assert.True(commands.IndexOf(cloneStart) < commands.IndexOf(cloneRemove));
        Assert.True(commands.IndexOf(cloneRemove) < commands.IndexOf(workerCreate));

        var output = new OutputCapture();
        var staged = await prepared.RunAndStageAsync(output, null, null);
        Assert.Contains("worker log", output.Lines);
        Assert.Equal("Example", staged.AssemblyName);
        Assert.Equal("{}", staged.ManifestJson.Trim());
        Assert.Equal("https://gitlab.com/example/plugin", staged.BuildEnvironment["gitRepository"]?.Value<string>());
        Assert.Equal("main", staged.BuildEnvironment["gitRef"]?.Value<string>());
        Assert.Equal("src/Plugin", staged.BuildEnvironment["pluginDir"]?.Value<string>());
        Assert.Equal("Release", staged.BuildEnvironment["buildConfig"]?.Value<string>());
        Assert.Equal(new string('a', 64), staged.BuildEnvironment["buildHash"]?.Value<string>());
        Assert.Equal(new string('b', 40), staged.BuildEnvironment["gitCommit"]?.Value<string>());
        Assert.Equal(
            DateTimeOffset.Parse("2026-08-27T12:34:56Z").UtcDateTime,
            staged.BuildEnvironment["gitCommitDate"]!.Value<DateTime>().ToUniversalTime());
        Assert.Equal(
            DateTimeOffset.Parse("2026-09-04T01:02:03Z").UtcDateTime,
            staged.BuildEnvironment["buildDate"]!.Value<DateTime>().ToUniversalTime());

        var workerStartParent = await fakeDocker.ReadWorkerStartParent();
        Assert.Contains("/usr/bin/perl", workerStartParent, StringComparison.Ordinal);
        Assert.Contains(BuildExecutorDocker.OutputLimiterPath, workerStartParent, StringComparison.Ordinal);
        Assert.Contains(
            $"{DockerBuildSandbox.MaxBuildLogBytes} {DockerBuildSandbox.MaxBuildLogLineBytes} " +
            $"{DockerBuildSandbox.MaxBuildLogLines} 900 -- docker container start --attach {prepared.WorkerContainer}",
            workerStartParent,
            StringComparison.Ordinal);

        commands = await fakeDocker.ReadCommands();
        var stagerCreate = Assert.Single(
            commands,
            command => command.StartsWith("container create --name pb-stager-", StringComparison.Ordinal));
        Assert.Contains("--network none", stagerCreate, StringComparison.Ordinal);
        Assert.Contains("--runtime runsc", stagerCreate, StringComparison.Ordinal);
        var workerRemove = $"container rm --force {prepared.WorkerContainer}";
        Assert.Contains(workerRemove, commands);
        Assert.True(commands.IndexOf(workerRemove) < commands.IndexOf(stagerCreate));
        Assert.Contains(
            $"type=bind,source={prepared.OutputVolume},target=/untrusted-output,readonly",
            stagerCreate,
            StringComparison.Ordinal);
        Assert.Contains(
            $"type=bind,source={prepared.SourceVolume},target=/source,readonly",
            stagerCreate,
            StringComparison.Ordinal);
        Assert.Contains(
            $"type=bind,source={prepared.StagingVolume},target=/staging",
            stagerCreate,
            StringComparison.Ordinal);
        Assert.Contains("--entrypoint /stage-artifacts.sh", stagerCreate, StringComparison.Ordinal);
        Assert.DoesNotContain(commands, command => command.Contains("pb-reader-", StringComparison.Ordinal));
        // The fixture publishes staged files only when removing the stager, so
        // successful reads also prove no writer remains when the app reads them.
        var stagerName = stagerCreate.Split(' ', StringSplitOptions.RemoveEmptyEntries)[3];
        Assert.Contains($"container rm --force {stagerName}", commands);

        await prepared.DisposeAsync();

        commands = await fakeDocker.ReadCommands();
        Assert.Contains($"container rm --force {prepared.WorkerContainer}", commands);
        Assert.Contains($"container rm --force {prepared.CloneContainer}", commands);
        Assert.Contains($"container rm --force {prepared.ProxyContainer}", commands);
        Assert.Contains($"network rm {prepared.InternalNetwork}", commands);
        Assert.Contains($"network rm {prepared.EgressNetwork}", commands);
        Assert.False(Directory.Exists(prepared.ScratchDirectory));
        Assert.True(state.Snapshot.IsReady);
    }

    [Fact]
    public async Task CheckoutOwnsSourceAndWorkerAndStagerOnlyReceiveItReadOnly()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create();
        var prepared = await CreateSandbox(fakeDocker, ReadyExecutor()).PrepareAsync(BuildId(), BuildInfo());

        try
        {
            var commands = await fakeDocker.ReadCommands();
            var initializerCreate = Assert.Single(
                commands,
                command => command.StartsWith("container create --name pb-scratch-init-", StringComparison.Ordinal));
            Assert.Contains("--user 0:0", initializerCreate, StringComparison.Ordinal);
            Assert.Contains("--runtime runsc", initializerCreate, StringComparison.Ordinal);
            Assert.Contains("--network none", initializerCreate, StringComparison.Ordinal);
            var sourceMode = initializerCreate.IndexOf(
                "chmod 0755 /scratch/source",
                StringComparison.Ordinal);
            var sourceOwner = initializerCreate.IndexOf(
                "chown 10002:10002 /scratch/source",
                StringComparison.Ordinal);
            Assert.True(sourceMode >= 0 && sourceMode < sourceOwner);
            Assert.Contains(
                "chown 10001:10001 /scratch/work /scratch/output /scratch/staging",
                initializerCreate,
                StringComparison.Ordinal);

            var cloneCreate = Assert.Single(
                commands,
                command => command.StartsWith(
                    $"container create --name {prepared.CloneContainer} ",
                    StringComparison.Ordinal));
            Assert.Contains("--user 10002:10002", cloneCreate, StringComparison.Ordinal);
            Assert.Contains(
                $"type=bind,source={prepared.SourceVolume},target=/source",
                cloneCreate,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                $"type=bind,source={prepared.SourceVolume},target=/source,readonly",
                cloneCreate,
                StringComparison.Ordinal);

            var workerCreate = Assert.Single(
                commands,
                command => command.StartsWith(
                    $"container create --name {prepared.WorkerContainer} ",
                    StringComparison.Ordinal));
            Assert.Contains("--user 10001:10001", workerCreate, StringComparison.Ordinal);
            Assert.Contains(
                $"type=bind,source={prepared.SourceVolume},target=/source,readonly",
                workerCreate,
                StringComparison.Ordinal);

            await prepared.RunAndStageAsync(new OutputCapture(), null, null);
            commands = await fakeDocker.ReadCommands();

            var stagerCreate = Assert.Single(
                commands,
                command => command.StartsWith("container create --name pb-stager-", StringComparison.Ordinal));
            Assert.Contains("--user 10001:10001", stagerCreate, StringComparison.Ordinal);
            Assert.Contains(
                $"type=bind,source={prepared.SourceVolume},target=/source,readonly",
                stagerCreate,
                StringComparison.Ordinal);
        }
        finally
        {
            await prepared.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task MissingBuildConfigIsNormalizedForWorkerAndTrustedProvenance(string? buildConfig)
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create();
        var buildInfo = BuildInfo();
        buildInfo.BuildConfig = buildConfig;
        var prepared = await CreateSandbox(fakeDocker, ReadyExecutor()).PrepareAsync(BuildId(), buildInfo);

        try
        {
            Assert.Equal("Release", buildInfo.BuildConfig);

            var commands = await fakeDocker.ReadCommands();
            var workerCreate = Assert.Single(
                commands,
                command => command.StartsWith(
                    $"container create --name {prepared.WorkerContainer} ",
                    StringComparison.Ordinal));
            Assert.Contains("--env BUILD_CONFIG=Release", workerCreate, StringComparison.Ordinal);

            var staged = await prepared.RunAndStageAsync(new OutputCapture(), null, null);
            Assert.Equal("Release", staged.BuildEnvironment["buildConfig"]?.Value<string>());
        }
        finally
        {
            await prepared.DisposeAsync();
        }
    }

    [Fact]
    public async Task ProxyReadinessFailureCleansTopologyBeforeAnyCheckoutOrWorkerIsCreated()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create(failProxyReadiness: true);
        var state = ReadyExecutor();

        var exception = await Assert.ThrowsAsync<BuildServiceException>(() =>
            CreateSandbox(fakeDocker, state).PrepareAsync(BuildId(), BuildInfo()));

        Assert.Contains("proxy did not become ready", exception.Message, StringComparison.OrdinalIgnoreCase);
        var commands = await fakeDocker.ReadCommands();
        Assert.DoesNotContain(
            commands,
            command => command.StartsWith("container create --name pb-clone-", StringComparison.Ordinal));
        Assert.DoesNotContain(
            commands,
            command => command.StartsWith("container create --name pb-worker-", StringComparison.Ordinal));
        Assert.Contains(
            commands,
            command => command.StartsWith("container rm --force pb-proxy-", StringComparison.Ordinal));
        Assert.Equal(2, commands.Count(command => command.StartsWith("network rm pb-", StringComparison.Ordinal)));
        Assert.Empty(Directory.EnumerateDirectories(fakeDocker.Directory, "pb-build-*", SearchOption.AllDirectories));
        Assert.True(state.Snapshot.IsReady);
    }

    [Fact]
    public async Task CheckoutFailureRemovesCloneAndNeverCreatesWorker()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create(failCloneStart: true);
        var state = ReadyExecutor();

        var exception = await Assert.ThrowsAsync<BuildServiceException>(() =>
            CreateSandbox(fakeDocker, state).PrepareAsync(BuildId(), BuildInfo()));

        Assert.Contains("repository checkout failed", exception.Message, StringComparison.OrdinalIgnoreCase);
        var commands = await fakeDocker.ReadCommands();
        Assert.Contains(
            commands,
            command => command.StartsWith("container start --attach pb-clone-", StringComparison.Ordinal));
        Assert.Contains(
            commands,
            command => command.StartsWith("container rm --force pb-clone-", StringComparison.Ordinal));
        Assert.DoesNotContain(
            commands,
            command => command.StartsWith("container create --name pb-worker-", StringComparison.Ordinal));
        Assert.Empty(Directory.EnumerateDirectories(fakeDocker.Directory, "pb-build-*", SearchOption.AllDirectories));
        Assert.True(state.Snapshot.IsReady);
    }

    [Theory]
    [InlineData("State.Running", "Inspecting the build proxy failed.")]
    [InlineData("IPAddress", "Reading the build proxy address failed.")]
    public async Task ProxyInspectionFailureKeepsSafeErrorAndCleansTopology(string inspection, string expectedError)
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create(failInspection: inspection);
        var state = ReadyExecutor();

        var exception = await Assert.ThrowsAsync<BuildServiceException>(() =>
            CreateSandbox(fakeDocker, state).PrepareAsync(BuildId(), BuildInfo()));

        Assert.Equal(expectedError, exception.Message);
        var commands = await fakeDocker.ReadCommands();
        Assert.Contains(commands, command => command.StartsWith("container inspect ", StringComparison.Ordinal));
        Assert.DoesNotContain(commands, command =>
            command.StartsWith("container create --name pb-clone-", StringComparison.Ordinal) ||
            command.StartsWith("container create --name pb-worker-", StringComparison.Ordinal));
        Assert.Contains(commands, command => command.StartsWith("container rm --force pb-proxy-", StringComparison.Ordinal));
        Assert.Equal(2, commands.Count(command => command.StartsWith("network rm pb-", StringComparison.Ordinal)));
        Assert.Empty(Directory.EnumerateDirectories(fakeDocker.Directory, "pb-build-*", SearchOption.AllDirectories));
        Assert.True(state.Snapshot.IsReady);
    }

    [Fact]
    public async Task FailedStagingNeverReturnsPartialOutputAndDisposalCleansIt()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create(failStagerStart: true);
        var state = ReadyExecutor();
        var prepared = await CreateSandbox(fakeDocker, state).PrepareAsync(BuildId(), BuildInfo());
        try
        {
            var exception = await Assert.ThrowsAsync<BuildServiceException>(() =>
                prepared.RunAndStageAsync(new OutputCapture(), null, null));

            Assert.Equal("Plugin artifact validation and staging failed.", exception.Message);
            // A partial canonical file is not an accepted output: no metadata
            // parsing or upload handoff may follow the staging command failure.
            Assert.Equal("{partial", await File.ReadAllTextAsync(Path.Combine(prepared.StagingVolume, "build-env.json")));
            var commands = await fakeDocker.ReadCommands();
            Assert.Contains(commands, command => command.StartsWith("container rm --force pb-stager-", StringComparison.Ordinal));
        }
        finally
        {
            await prepared.DisposeAsync();
        }

        Assert.False(Directory.Exists(prepared.ScratchDirectory));
        Assert.True(state.Snapshot.IsReady);
    }

    [Fact]
    public async Task AmbiguousProxyCreateRetriesRemovalWhenContainerAppearsAfterInitialNotFound()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create(ambiguousProxyCreate: true);
        var state = ReadyExecutor();
        var prepare = CreateSandbox(fakeDocker, state).PrepareAsync(BuildId(), BuildInfo());

        await fakeDocker.WaitForAmbiguousCreate();
        state.MarkUnavailable("Simulated executor shutdown during docker create");

        var exception = await Assert.ThrowsAnyAsync<BuildServiceException>(() => prepare);
        Assert.Contains("stopped", exception.Message, StringComparison.OrdinalIgnoreCase);

        var commands = await fakeDocker.ReadCommands();
        var proxyCreate = Assert.Single(
            commands,
            command => command.StartsWith("container create --name pb-proxy-", StringComparison.Ordinal));
        var proxyName = proxyCreate.Split(' ', StringSplitOptions.RemoveEmptyEntries)[3];
        Assert.Equal(
            2,
            commands.Count(command => command == $"container rm --force {proxyName}"));
        Assert.False(fakeDocker.AmbiguousContainerExists);
        Assert.DoesNotContain(
            commands,
            command => command.StartsWith("container create --name pb-clone-", StringComparison.Ordinal));
        Assert.False(state.Snapshot.IsReady);
        Assert.Equal(
            "A docker create operation had an ambiguous result",
            state.Snapshot.UnavailableReason);
    }

    [Theory]
    [InlineData(
        "not-an-object-id",
        "2026-08-27T12:34:56Z",
        "2026-09-04T01:02:03Z",
        "invalid Git commit")]
    [InlineData(
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        "not-a-timestamp",
        "2026-09-04T01:02:03Z",
        "invalid timestamp")]
    [InlineData(
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        "2026-08-27T12:34:56Z",
        "not-a-timestamp",
        "invalid timestamp")]
    public async Task InvalidStagedProvenanceIsRejectedAndSandboxIsCleaned(
        string gitCommit,
        string gitCommitDate,
        string buildDate,
        string expectedError)
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create(
            gitCommit: gitCommit,
            gitCommitDate: gitCommitDate,
            buildDate: buildDate);
        var state = ReadyExecutor();
        var prepared = await CreateSandbox(fakeDocker, state).PrepareAsync(BuildId(), BuildInfo());

        try
        {
            var exception = await Assert.ThrowsAsync<BuildServiceException>(() =>
                prepared.RunAndStageAsync(new OutputCapture(), null, null));
            Assert.Contains(expectedError, exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await prepared.DisposeAsync();
        }

        Assert.False(Directory.Exists(prepared.ScratchDirectory));
        Assert.True(state.Snapshot.IsReady);
    }

    [Fact]
    public async Task ConcurrentBuildsLeaseSeparateSlotsUntilStagingAndCleanupFinish()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create();
        var sandbox = CreateSandbox(fakeDocker, ReadyExecutor());
        await using var first = await sandbox.PrepareAsync(BuildId(), BuildInfo());
        await using var second = await sandbox.PrepareAsync(new FullBuildId("sandbox-test", 8), BuildInfo());
        var firstSlot = Path.GetDirectoryName(first.ScratchDirectory);
        var secondSlot = Path.GetDirectoryName(second.ScratchDirectory);
        Assert.Equal(DockerBuildSandbox.ScratchSlotPath(fakeDocker.Directory, 0), firstSlot);
        Assert.Equal(DockerBuildSandbox.ScratchSlotPath(fakeDocker.Directory, 1), secondSlot);
        await Assert.ThrowsAsync<BuildServiceException>(() => sandbox.PrepareAsync(BuildId(), BuildInfo()));

        await first.RunAndStageAsync(new OutputCapture(), null, null);
        // The artifact is still awaiting upload. Finishing compilation must not release its slot.
        await Assert.ThrowsAsync<BuildServiceException>(() => sandbox.PrepareAsync(BuildId(), BuildInfo()));
        await first.DisposeAsync();
        Assert.False(Directory.Exists(first.ScratchDirectory));
        Assert.True(Directory.Exists(second.ScratchDirectory));

        await using var replacement = await sandbox.PrepareAsync(BuildId(), BuildInfo());
        Assert.Equal(firstSlot, Path.GetDirectoryName(replacement.ScratchDirectory));
        Assert.NotEqual(first.ScratchDirectory, replacement.ScratchDirectory);
    }

    public static IEnumerable<object[]> InvalidStagedFiles()
    {
        foreach (var file in new[] { "build-env.json", "manifest.json", "artifact.sha256" })
        foreach (var kind in new[] { "missing", "empty", "oversized", "directory", "symlink", "broken-symlink", "fifo" })
            yield return [file, kind];
    }

    [Theory]
    [MemberData(nameof(InvalidStagedFiles))]
    public async Task LocalMetadataReadsRejectInvalidFilesAndCleanSandbox(string file, string kind)
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create(invalidStagedFile: file, invalidStagedKind: kind);
        var state = ReadyExecutor();
        var prepared = await CreateSandbox(fakeDocker, state).PrepareAsync(BuildId(), BuildInfo());
        try
        {
            var exception = await Assert.ThrowsAsync<BuildServiceException>(() =>
                prepared.RunAndStageAsync(new OutputCapture(), null, null).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Contains($"Staged file '{file}' must be a nonempty regular file", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            await prepared.DisposeAsync();
        }

        Assert.False(Directory.Exists(prepared.ScratchDirectory));
        Assert.True(state.Snapshot.IsReady);
    }

    [Fact]
    public async Task LocalMetadataReadsRejectLinkedStagingDirectory()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create(invalidStagedKind: "linked-staging");
        var state = ReadyExecutor();
        var prepared = await CreateSandbox(fakeDocker, state).PrepareAsync(BuildId(), BuildInfo());
        var exception = await Assert.ThrowsAsync<BuildServiceException>(() =>
            prepared.RunAndStageAsync(new OutputCapture(), null, null));
        Assert.Contains("metadata staging directory is unavailable", exception.Message, StringComparison.Ordinal);
        // Cleanup must also refuse this malformed mount and retain the slot.
        await Assert.ThrowsAsync<BuildServiceException>(() => prepared.DisposeAsync().AsTask());
        Assert.False(state.Snapshot.IsReady);
    }

    [Fact]
    public async Task LocalMetadataReadsPreserveUtf8BomSupport()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create(invalidStagedKind: "utf8-bom");
        await using var prepared = await CreateSandbox(fakeDocker, ReadyExecutor()).PrepareAsync(BuildId(), BuildInfo());
        var output = await prepared.RunAndStageAsync(new OutputCapture(), null, null);
        Assert.Equal("{}", output.ManifestJson.Trim());
    }

    [Fact]
    public async Task FailedStagerRemovalPreventsLocalMetadataReads()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create(failStagerRemoval: true);
        var state = ReadyExecutor();
        var prepared = await CreateSandbox(fakeDocker, state).PrepareAsync(BuildId(), BuildInfo());
        var exception = await Assert.ThrowsAsync<BuildServiceException>(() =>
            prepared.RunAndStageAsync(new OutputCapture(), null, null));
        Assert.Contains("staging container could not be removed safely", exception.Message, StringComparison.Ordinal);
        Assert.False(state.Snapshot.IsReady);
        await Assert.ThrowsAsync<BuildServiceException>(() => prepared.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task FailedCleanupDisablesExecutorAndDoesNotReuseDirtySlot()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create(failWorkerRemoval: true);
        var state = ReadyExecutor();
        var sandbox = CreateSandbox(fakeDocker, state);
        var first = await sandbox.PrepareAsync(BuildId(), BuildInfo());
        var exception = await Assert.ThrowsAsync<BuildServiceException>(() => first.DisposeAsync().AsTask());
        Assert.Contains("clean up", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(state.Snapshot.IsReady);
        Assert.Null(state.Snapshot.WorkerImageId);
        Assert.Null(state.Snapshot.ProxyImageId);
        Assert.Contains("could not be cleaned up", state.Snapshot.UnavailableReason, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(first.ScratchDirectory));
        await Assert.ThrowsAsync<BuildServiceException>(() => sandbox.PrepareAsync(BuildId(), BuildInfo()));

        // Even if a caller resets readiness, the failed slot remains leased in this executor.
        state.MarkReady(WorkerImageId, ProxyImageId);
        var second = await sandbox.PrepareAsync(BuildId(), BuildInfo());
        Assert.NotEqual(Path.GetDirectoryName(first.ScratchDirectory), Path.GetDirectoryName(second.ScratchDirectory));
        await Assert.ThrowsAsync<BuildServiceException>(() => second.DisposeAsync().AsTask());
    }

    private DockerBuildSandbox CreateSandbox(FakeDocker fakeDocker, BuildExecutorState state)
    {
        for (var slot = 0; slot < DockerBuildSandbox.MaxConcurrentBuilds; slot++)
            Directory.CreateDirectory(DockerBuildSandbox.ScratchSlotPath(fakeDocker.Directory, slot));
        var processRunner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
        return new DockerBuildSandbox(
            _log.CreateLogger<DockerBuildSandbox>(),
            new PluginBuilderOptions
            {
                DataDir = fakeDocker.Directory,
                BuildScratchRoot = fakeDocker.Directory,
                BuildTimeout = TimeSpan.FromMinutes(15)
            },
            processRunner,
            state,
            new BuildScratchCleaner(
                NullLogger<BuildScratchCleaner>.Instance,
                processRunner));
    }

    private static BuildExecutorState ReadyExecutor()
    {
        var state = new BuildExecutorState();
        state.MarkReady(WorkerImageId, ProxyImageId);
        return state;
    }

    private static FullBuildId BuildId() => new("sandbox-test", 7);

    private static BuildInfo BuildInfo() => new()
    {
        GitRepository = "https://gitlab.com/example/plugin",
        GitRef = "main",
        PluginDir = "src/Plugin",
        BuildConfig = "Release"
    };

    internal sealed class FakeDocker : IAsyncDisposable
    {
        private readonly Dictionary<string, string?> _originalEnvironment;

        private FakeDocker(string directory, Dictionary<string, string?> originalEnvironment)
        {
            Directory = directory;
            _originalEnvironment = originalEnvironment;
        }

        public string Directory { get; }

        public static async Task<FakeDocker> Create(
            bool failWorkerRemoval = false,
            bool failProxyReadiness = false,
            bool failCloneStart = false,
            bool ambiguousProxyCreate = false,
            string gitCommit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            string gitCommitDate = "2026-08-27T12:34:56Z",
            string buildDate = "2026-09-04T01:02:03Z",
            string invalidStagedFile = "manifest.json",
            string invalidStagedKind = "",
            bool failStagerRemoval = false,
            string failInspection = "",
            bool failStagerStart = false,
            bool failScratchCleanup = false,
            string manifestJson = "{}")
        {
            var directory = Path.Combine(Path.GetTempPath(), $"plugin-builder-sandbox-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(directory);
            var dockerPath = Path.Combine(directory, "docker");
            await File.WriteAllTextAsync(dockerPath, """
                #!/bin/sh
                set -eu
                commands="${PB_FAKE_DOCKER_COMMANDS:?}"
                printf '%s\n' "$*" >> "$commands"

                case "$1:$2" in
                    network:create|network:connect|network:rm)
                        ;;
                    container:create)
                        previous=""
                        name=""
                        for argument in "$@"; do
                            if [ "$previous" = "--name" ]; then name="$argument"; break; fi
                            previous="$argument"
                        done
                        [ -n "$name" ]
                        printf '%s\n' "$*" > "${commands}.create.${name}"
                        case "$name" in
                            pb-proxy-*)
                                if [ "${PB_FAKE_AMBIGUOUS_PROXY_CREATE:-false}" = "true" ]; then
                                    : > "${commands}.ambiguous-create-started"
                                    sleep 60
                                fi
                                ;;
                        esac
                        ;;
                    container:start)
                        target=""
                        for argument in "$@"; do target="$argument"; done
                        case "$target" in
                            pb-scratch-clean-*)
                                if [ "${PB_FAKE_FAIL_SCRATCH_CLEANUP:-false}" = "true" ]; then
                                    printf '%s\n' 'simulated scratch cleanup failure' >&2
                                    exit 23
                                fi
                                create="$(cat "${commands}.create.${target}")"
                                for argument in $create; do
                                    case "$argument" in
                                        type=bind,source=*,target=*)
                                            source="${argument#type=bind,source=}"
                                            source="${source%,target=*}"
                                            case "$source" in
                                                "${commands%/commands}"/slot-*/pb-build-*/*)
                                                    find "$source" -xdev -mindepth 1 -delete
                                                    ;;
                                                *) exit 3 ;;
                                            esac
                                            ;;
                                    esac
                                done
                                ;;
                            pb-worker-*)
                                ps -p "$PPID" -o command= > "${commands}.worker-parent"
                                printf '%s\n' 'worker log'
                                ;;
                            pb-proxy-*)
                                create="$(cat "${commands}.create.${target}")"
                                resolver_source=""
                                for argument in $create; do
                                    case "$argument" in
                                        type=bind,source=*,target=/etc/resolv.conf,readonly)
                                            resolver_source="${argument#type=bind,source=}"
                                            resolver_source="${resolver_source%,target=/etc/resolv.conf,readonly}"
                                            ;;
                                    esac
                                done
                                [ -n "$resolver_source" ]
                                cp -- "$resolver_source" "${commands}.proxy-resolv.${target}"
                                ;;
                            pb-clone-*)
                                if [ "${PB_FAKE_FAIL_CLONE_START:-false}" = "true" ]; then
                                    printf '%s\n' 'simulated checkout failure' >&2
                                    exit 29
                                fi
                                ;;
                            pb-stager-*)
                                if [ "${PB_FAKE_FAIL_STAGER_START:-false}" = "true" ]; then
                                    printf '%s\n' 'simulated staging write failure' >&2
                                    exit 28
                                fi
                                ;;
                        esac
                        ;;
                    container:exec)
                        if [ "${PB_FAKE_FAIL_PROXY_READINESS:-false}" = "true" ]; then
                            exit 31
                        fi
                        ;;
                    container:inspect)
                        if [ -n "${PB_FAKE_FAIL_INSPECTION:-}" ]; then
                            case "$*" in
                                *"${PB_FAKE_FAIL_INSPECTION}"*)
                                    printf '%s\n' 'private docker inspection details' >&2
                                    exit 27
                                    ;;
                            esac
                        fi
                        case "$*" in
                            *State.Running*) printf '%s\n' true ;;
                            *IPAddress*) printf '%s\n' 172.31.0.2 ;;
                            *) exit 2 ;;
                        esac
                        ;;
                    container:rm)
                        target=""
                        for argument in "$@"; do target="$argument"; done
                        case "$target" in
                            pb-stager-*)
                                if [ "${PB_FAKE_FAIL_STAGER_REMOVE:-false}" = "true" ]; then
                                    printf '%s\n' 'simulated stager cleanup failure' >&2
                                    exit 17
                                fi
                                create="$(cat "${commands}.create.${target}")"
                                staging=""
                                for argument in $create; do
                                    case "$argument" in
                                        type=bind,source=*,target=/staging)
                                            staging="${argument#type=bind,source=}"
                                            staging="${staging%,target=/staging}"
                                            ;;
                                    esac
                                done
                                [ -n "$staging" ]
                                if [ "${PB_FAKE_FAIL_STAGER_START:-false}" = "true" ]; then
                                    printf '%s' '{partial' > "$staging/build-env.json"
                                    exit 0
                                fi
                                printf '%s\n' "{\"assemblyName\":\"Example\",\"gitCommit\":\"${PB_FAKE_GIT_COMMIT:?}\",\"gitCommitDate\":\"${PB_FAKE_GIT_COMMIT_DATE:?}\",\"buildDate\":\"${PB_FAKE_BUILD_DATE:?}\",\"buildHash\":\"untrusted\",\"gitRepository\":\"https://attacker.invalid/repo\",\"gitRef\":\"attacker\",\"pluginDir\":\"attacker\",\"buildConfig\":\"Debug\"}" > "$staging/build-env.json"
                                printf '%s\n' "${PB_FAKE_MANIFEST_JSON:?}" > "$staging/manifest.json"
                                printf '%s' 'canonical artifact' > "$staging/artifact.btcpay"
                                printf '%064d\n' 0 | tr 0 a > "$staging/artifact.sha256"
                                metadata="$staging/${PB_FAKE_INVALID_STAGED_FILE:?}"
                                case "${PB_FAKE_INVALID_STAGED_KIND:-}" in
                                    missing) rm -- "$metadata" ;;
                                    empty) : > "$metadata" ;;
                                    oversized) dd if=/dev/zero of="$metadata" bs=1048577 count=1 2>/dev/null ;;
                                    directory) rm -- "$metadata"; mkdir -- "$metadata" ;;
                                    symlink) rm -- "$metadata"; ln -s "$commands" "$metadata" ;;
                                    broken-symlink) rm -- "$metadata"; ln -s "$staging/missing" "$metadata" ;;
                                    fifo) rm -- "$metadata"; mkfifo -- "$metadata" ;;
                                    utf8-bom) printf '\357\273\277{}\n' > "$metadata" ;;
                                    linked-staging) mv -- "$staging" "$staging.actual"; ln -s "$staging.actual" "$staging" ;;
                                esac
                                ;;
                            pb-proxy-*)
                                if [ "${PB_FAKE_AMBIGUOUS_PROXY_CREATE:-false}" = "true" ]; then
                                    if [ ! -f "${commands}.ambiguous-remove-attempted" ]; then
                                        : > "${commands}.ambiguous-remove-attempted"
                                        : > "${commands}.ambiguous-container-exists"
                                        printf '%s\n' "Error response from daemon: No such container: $target" >&2
                                        exit 1
                                    fi
                                    rm -f "${commands}.ambiguous-container-exists"
                                fi
                                ;;
                            pb-worker-*)
                                if [ "${PB_FAKE_FAIL_WORKER_REMOVE:-false}" = "true" ]; then
                                    printf '%s\n' 'simulated worker cleanup failure' >&2
                                    exit 17
                                fi
                                ;;
                        esac
                        ;;
                    *)
                        exit 2
                        ;;
                esac
                """);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(
                    dockerPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            string[] keys =
            [
                "PATH",
                "PB_FAKE_DOCKER_COMMANDS",
                "PB_FAKE_FAIL_WORKER_REMOVE",
                "PB_FAKE_FAIL_PROXY_READINESS",
                "PB_FAKE_FAIL_CLONE_START",
                "PB_FAKE_AMBIGUOUS_PROXY_CREATE",
                "PB_FAKE_GIT_COMMIT",
                "PB_FAKE_GIT_COMMIT_DATE",
                "PB_FAKE_BUILD_DATE",
                "PB_FAKE_INVALID_STAGED_FILE",
                "PB_FAKE_INVALID_STAGED_KIND",
                "PB_FAKE_FAIL_STAGER_REMOVE",
                "PB_FAKE_FAIL_INSPECTION",
                "PB_FAKE_FAIL_STAGER_START",
                "PB_FAKE_FAIL_SCRATCH_CLEANUP",
                "PB_FAKE_MANIFEST_JSON"
            ];
            var originalEnvironment = keys.ToDictionary(
                key => key,
                Environment.GetEnvironmentVariable);
            Environment.SetEnvironmentVariable(
                "PATH",
                directory + Path.PathSeparator + originalEnvironment["PATH"]);
            Environment.SetEnvironmentVariable(
                "PB_FAKE_DOCKER_COMMANDS",
                Path.Combine(directory, "commands"));
            Environment.SetEnvironmentVariable(
                "PB_FAKE_FAIL_WORKER_REMOVE",
                failWorkerRemoval ? "true" : "false");
            Environment.SetEnvironmentVariable(
                "PB_FAKE_FAIL_PROXY_READINESS",
                failProxyReadiness ? "true" : "false");
            Environment.SetEnvironmentVariable(
                "PB_FAKE_FAIL_CLONE_START",
                failCloneStart ? "true" : "false");
            Environment.SetEnvironmentVariable(
                "PB_FAKE_AMBIGUOUS_PROXY_CREATE",
                ambiguousProxyCreate ? "true" : "false");
            Environment.SetEnvironmentVariable("PB_FAKE_GIT_COMMIT", gitCommit);
            Environment.SetEnvironmentVariable("PB_FAKE_GIT_COMMIT_DATE", gitCommitDate);
            Environment.SetEnvironmentVariable("PB_FAKE_BUILD_DATE", buildDate);
            Environment.SetEnvironmentVariable("PB_FAKE_INVALID_STAGED_FILE", invalidStagedFile);
            Environment.SetEnvironmentVariable("PB_FAKE_INVALID_STAGED_KIND", invalidStagedKind);
            Environment.SetEnvironmentVariable("PB_FAKE_FAIL_STAGER_REMOVE", failStagerRemoval ? "true" : "false");
            Environment.SetEnvironmentVariable("PB_FAKE_FAIL_INSPECTION", failInspection);
            Environment.SetEnvironmentVariable("PB_FAKE_FAIL_STAGER_START", failStagerStart ? "true" : "false");
            Environment.SetEnvironmentVariable("PB_FAKE_FAIL_SCRATCH_CLEANUP", failScratchCleanup ? "true" : "false");
            Environment.SetEnvironmentVariable("PB_FAKE_MANIFEST_JSON", manifestJson);

            return new FakeDocker(directory, originalEnvironment);
        }

        public bool AmbiguousContainerExists =>
            File.Exists(Path.Combine(Directory, "commands.ambiguous-container-exists"));

        public async Task WaitForAmbiguousCreate()
        {
            var marker = Path.Combine(Directory, "commands.ambiguous-create-started");
            for (var attempt = 0; attempt < 500; attempt++)
            {
                if (File.Exists(marker))
                    return;
                await Task.Delay(10);
            }

            throw new TimeoutException("Fake docker create was not reached");
        }

        public async Task<List<string>> ReadCommands()
        {
            var path = Path.Combine(Directory, "commands");
            return File.Exists(path)
                ? (await File.ReadAllLinesAsync(path)).ToList()
                : [];
        }

        public async Task<string> ReadWorkerStartParent()
        {
            return await File.ReadAllTextAsync(Path.Combine(Directory, "commands.worker-parent"));
        }

        public async Task<string> ReadProxyResolverConfiguration()
        {
            return await File.ReadAllTextAsync(Assert.Single(
                System.IO.Directory.EnumerateFiles(Directory, "commands.proxy-resolv.*")));
        }

        public ValueTask DisposeAsync()
        {
            foreach (var (key, value) in _originalEnvironment)
                Environment.SetEnvironmentVariable(key, value);
            System.IO.Directory.Delete(Directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
