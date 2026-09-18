using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using PluginBuilder.BuildBroker.Configuration;
using PluginBuilder.Util;
using Xunit;
using Xunit.Abstractions;

using PluginBuilder.BuildBroker;
using PluginBuilder.BuildBroker.Services;
using PluginBuilder.Builds.Services;

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrepareCreatesPerBuildProxyTopologyAndDisposeCleansEveryResource(bool useRunc)
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create();
        var state = ReadyExecutor();
        var sandbox = CreateSandbox(fakeDocker, state, useRunc: useRunc);
        var runtime = useRunc ? "runc" : "runsc";
        await using var prepared = await sandbox.PrepareAsync(BuildId(), BuildInfo());

        Assert.StartsWith("pb-worker-", prepared.WorkerContainer, StringComparison.Ordinal);
        Assert.StartsWith("pb-clone-", prepared.CloneContainer, StringComparison.Ordinal);
        Assert.StartsWith("pb-proxy-", prepared.ProxyContainer, StringComparison.Ordinal);
        Assert.StartsWith("pb-internal-", prepared.InternalNetwork, StringComparison.Ordinal);
        Assert.StartsWith("pb-egress-", prepared.EgressNetwork, StringComparison.Ordinal);
        Assert.NotEqual(prepared.InternalNetwork, prepared.EgressNetwork);

        var commands = await fakeDocker.ReadCommands();
        var initializerCreate = Assert.Single(
            commands,
            command => command.StartsWith("container create --name pb-scratch-init-", StringComparison.Ordinal));
        Assert.Contains("--user 0:0", initializerCreate, StringComparison.Ordinal);
        Assert.Contains($"--runtime {runtime}", initializerCreate, StringComparison.Ordinal);
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
        var proxyResolverFile = Path.Combine(prepared.WorkDirectory, ".proxy-resolv.conf");
        Assert.Contains($"--network {prepared.EgressNetwork}", proxyCreate, StringComparison.Ordinal);
        Assert.Contains(
            $"type=bind,source={proxyResolverFile},target=/etc/resolv.conf,readonly",
            proxyCreate,
            StringComparison.Ordinal);
        Assert.DoesNotContain("--dns", proxyCreate, StringComparison.Ordinal);
        Assert.Contains($"--runtime {runtime}", proxyCreate, StringComparison.Ordinal);
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
        Assert.Contains($"--runtime {runtime}", cloneCreate, StringComparison.Ordinal);
        Assert.Contains("--read-only", cloneCreate, StringComparison.Ordinal);
        Assert.Contains("--user 10002:10002", cloneCreate, StringComparison.Ordinal);
        Assert.Contains("--cap-drop ALL", cloneCreate, StringComparison.Ordinal);
        Assert.Contains("--security-opt no-new-privileges:true", cloneCreate, StringComparison.Ordinal);
        Assert.Contains("--memory 1g --memory-swap 1g", cloneCreate, StringComparison.Ordinal);
        Assert.Contains("--pids-limit 128", cloneCreate, StringComparison.Ordinal);
        Assert.Contains("--ulimit nofile=1024:1024", cloneCreate, StringComparison.Ordinal);
        Assert.Contains(
            $"type=bind,source={prepared.SourceDirectory},target=/source",
            cloneCreate,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"type=bind,source={prepared.SourceDirectory},target=/source,readonly",
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
        Assert.Contains("--user 10001:10001", workerCreate, StringComparison.Ordinal);
        Assert.Contains($"--runtime {runtime}", workerCreate, StringComparison.Ordinal);
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
            $"type=bind,source={prepared.SourceDirectory},target=/source,readonly",
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
        var staged = await prepared.RunAndStageAsync(output);
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

        commands = await fakeDocker.ReadCommands();
        var stagerCreate = Assert.Single(
            commands,
            command => command.StartsWith("container create --name pb-stager-", StringComparison.Ordinal));
        Assert.Contains("--user 10001:10001", stagerCreate, StringComparison.Ordinal);
        Assert.Contains("--network none", stagerCreate, StringComparison.Ordinal);
        Assert.Contains($"--runtime {runtime}", stagerCreate, StringComparison.Ordinal);
        var workerRemove = $"container rm --force {prepared.WorkerContainer}";
        Assert.Contains(workerRemove, commands);
        Assert.True(commands.IndexOf(workerRemove) < commands.IndexOf(stagerCreate));
        Assert.Contains(
            $"type=bind,source={prepared.OutputDirectory},target=/untrusted-output,readonly",
            stagerCreate,
            StringComparison.Ordinal);
        Assert.Contains(
            $"type=bind,source={prepared.SourceDirectory},target=/source,readonly",
            stagerCreate,
            StringComparison.Ordinal);
        Assert.Contains(
            $"type=bind,source={prepared.StagingDirectory},target=/staging",
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

            var staged = await prepared.RunAndStageAsync(new OutputCapture());
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
                prepared.RunAndStageAsync(new OutputCapture()));

            Assert.Equal("Plugin artifact validation and staging failed.", exception.Message);
            // A partial canonical file is not an accepted output: no metadata
            // parsing or upload handoff may follow the staging command failure.
            Assert.Equal("{partial", await File.ReadAllTextAsync(Path.Combine(prepared.StagingDirectory, "build-env.json")));
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
                prepared.RunAndStageAsync(new OutputCapture()));
            Assert.Contains(expectedError, exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await prepared.DisposeAsync();
        }

        Assert.False(Directory.Exists(prepared.ScratchDirectory));
        Assert.True(state.Snapshot.IsReady);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\\n")]
    public async Task InvalidStagedHashIsRejectedAndSandboxIsCleaned(string buildHash)
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create(buildHash: buildHash);
        var state = ReadyExecutor();
        var prepared = await CreateSandbox(fakeDocker, state).PrepareAsync(BuildId(), BuildInfo());
        try
        {
            var exception = await Assert.ThrowsAsync<BuildServiceException>(() =>
                prepared.RunAndStageAsync(new OutputCapture()));
            Assert.Contains("invalid SHA-256 digest", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            await prepared.DisposeAsync();
        }

        Assert.False(Directory.Exists(prepared.ScratchDirectory));
        Assert.True(state.Snapshot.IsReady);
    }

    [Fact]
    public async Task ConcurrentBuildsKeepSeparateDirectoriesUntilCleanupFinishes()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create();
        var sandbox = CreateSandbox(fakeDocker, ReadyExecutor());
        await using var first = await sandbox.PrepareAsync(BuildId(), BuildInfo());
        await using var second = await sandbox.PrepareAsync(new FullBuildId("sandbox-test", 8), BuildInfo());
        // Admission remains bounded by BrokerCoordinator and is covered by
        // BuildBrokerSecurityTests.ParallelClientsCannotAllocateMoreThanTwoLeases.
        Assert.Equal(fakeDocker.Directory, Path.GetDirectoryName(first.ScratchDirectory));
        Assert.Equal(fakeDocker.Directory, Path.GetDirectoryName(second.ScratchDirectory));
        Assert.NotEqual(first.ScratchDirectory, second.ScratchDirectory);

        await first.RunAndStageAsync(new OutputCapture());
        Assert.True(Directory.Exists(first.StagingDirectory));
        await first.DisposeAsync();
        Assert.False(Directory.Exists(first.ScratchDirectory));
        Assert.True(Directory.Exists(second.ScratchDirectory));

        await using var replacement = await sandbox.PrepareAsync(BuildId(), BuildInfo());
        Assert.Equal(fakeDocker.Directory, Path.GetDirectoryName(replacement.ScratchDirectory));
        Assert.NotEqual(first.ScratchDirectory, replacement.ScratchDirectory);
    }

    public static IEnumerable<object[]> InvalidStagedFiles()
    {
        foreach (var file in new[] { "build-env.json", "manifest.json" })
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
                prepared.RunAndStageAsync(new OutputCapture()).WaitAsync(TimeSpan.FromSeconds(10)));
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
            prepared.RunAndStageAsync(new OutputCapture()));
        Assert.Contains("metadata staging directory is unavailable", exception.Message, StringComparison.Ordinal);
        // Cleanup must also refuse this malformed mount and disable the executor.
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
        var output = await prepared.RunAndStageAsync(new OutputCapture());
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
            prepared.RunAndStageAsync(new OutputCapture()));
        Assert.Contains("staging container could not be removed safely", exception.Message, StringComparison.Ordinal);
        Assert.False(state.Snapshot.IsReady);
        await Assert.ThrowsAsync<BuildServiceException>(() => prepared.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task FailedCleanupDisablesExecutorAndPreservesDirtyDirectory()
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
    }

    [Theory]
    [InlineData(1, "Plugin build failed.")]
    [InlineData(2, "Plugin build failed.")]
    [InlineData(124, "Plugin build failed.")]
    [InlineData(78, "Plugin build failed.")]
    public async Task FailedWorkerIsRemovedBeforeReturningErrorAndNeverStagesArtifacts(
        int exitCode,
        string expectedMessage)
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create(workerExitCode: exitCode);
        var state = ReadyExecutor();
        var sandbox = CreateSandbox(fakeDocker, state);
        var prepared = await sandbox.PrepareAsync(BuildId(), BuildInfo());
        var output = new OutputCapture();
        try
        {
            var exception = await Assert.ThrowsAsync<BuildServiceException>(() =>
                prepared.RunAndStageAsync(output).WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.Equal(expectedMessage, exception.Message);
            var commands = await fakeDocker.ReadCommands();
            var start = $"container start --attach {prepared.WorkerContainer}";
            var remove = $"container rm --force {prepared.WorkerContainer}";
            Assert.Contains(start, commands);
            Assert.True(commands.IndexOf(start) < commands.IndexOf(remove));
            var workerCreate = Assert.Single(commands, command =>
                command.StartsWith($"container create --name {prepared.WorkerContainer} ", StringComparison.Ordinal));
            Assert.Equal(512, BuildPolicy.WorkerPidLimit);
            Assert.Contains("--pids-limit 512", workerCreate, StringComparison.Ordinal);
            Assert.DoesNotContain("--privileged", workerCreate, StringComparison.Ordinal);
            Assert.DoesNotContain("--cgroupns", workerCreate, StringComparison.Ordinal);
            Assert.DoesNotContain(commands, command => command.Contains("pb-stager-", StringComparison.Ordinal));
        }
        finally
        {
            await prepared.DisposeAsync();
        }

        Assert.False(Directory.Exists(prepared.ScratchDirectory));
        Assert.True(state.Snapshot.IsReady);
        var cleanedCommands = await fakeDocker.ReadCommands();
        Assert.Contains($"network rm {prepared.InternalNetwork}", cleanedCommands);
        Assert.Contains($"network rm {prepared.EgressNetwork}", cleanedCommands);
        var cleaner = cleanedCommands.FindIndex(command =>
            command.StartsWith("container create --name pb-scratch-clean-", StringComparison.Ordinal));
        Assert.True(cleanedCommands.IndexOf($"container rm --force {prepared.WorkerContainer}") < cleaner);
    }

    [Fact]
    public async Task FailedWorkerIsCleanedAndTheNextBuildSucceeds()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create(workerExitCode: 2);
        var state = ReadyExecutor();
        var sandbox = CreateSandbox(fakeDocker, state);
        var failed = await sandbox.PrepareAsync(BuildId(), BuildInfo());
        try
        {
            var exception = await Assert.ThrowsAsync<BuildServiceException>(() =>
                failed.RunAndStageAsync(new OutputCapture()));
            Assert.Equal("Plugin build failed.", exception.Message);
        }
        finally
        {
            await failed.DisposeAsync();
        }

        fakeDocker.SetWorkerResult(exitCode: 0);
        await using var replacement = await sandbox.PrepareAsync(new FullBuildId("sandbox-test", 8), BuildInfo());
        await using var peer = await sandbox.PrepareAsync(new FullBuildId("sandbox-test", 9), BuildInfo());
        Assert.NotEqual(failed.ScratchDirectory, replacement.ScratchDirectory);
        Assert.NotEqual(replacement.ScratchDirectory, peer.ScratchDirectory);
        Assert.Equal("Example", (await replacement.RunAndStageAsync(new OutputCapture())).AssemblyName);
        await replacement.DisposeAsync();
        await peer.DisposeAsync();
        Assert.False(Directory.Exists(failed.ScratchDirectory));
        Assert.False(Directory.Exists(replacement.ScratchDirectory));
        Assert.False(Directory.Exists(peer.ScratchDirectory));
        Assert.True(state.Snapshot.IsReady);
    }

    [Fact]
    public async Task FailedWorkerRemovalPreventsStagingAndDisablesExecutor()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create(workerExitCode: 2, failWorkerRemoval: true);
        var state = ReadyExecutor();
        var sandbox = CreateSandbox(fakeDocker, state);
        var prepared = await sandbox.PrepareAsync(BuildId(), BuildInfo());
        var exception = await Assert.ThrowsAsync<BuildServiceException>(() =>
            prepared.RunAndStageAsync(new OutputCapture()));
        Assert.Contains("worker could not be removed safely", exception.Message, StringComparison.Ordinal);
        Assert.False(state.Snapshot.IsReady);
        Assert.Null(state.Snapshot.WorkerImageId);
        Assert.Null(state.Snapshot.ProxyImageId);
        await Assert.ThrowsAsync<BuildServiceException>(() => prepared.DisposeAsync().AsTask());
        Assert.True(Directory.Exists(prepared.ScratchDirectory));
        Assert.DoesNotContain(await fakeDocker.ReadCommands(), command => command.Contains("pb-stager-", StringComparison.Ordinal));
        await Assert.ThrowsAsync<BuildServiceException>(() => sandbox.PrepareAsync(BuildId(), BuildInfo()));
    }

    [Fact]
    public async Task WorkerExecutionTimeoutStartsAfterDockerResourcesAreCreated()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create(delayWorkerCreate: true, stallWorker: true);
        var state = ReadyExecutor();
        var sandbox = CreateSandbox(fakeDocker, state, TimeSpan.FromSeconds(1));
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        // Creating the worker takes two seconds, longer than its execution budget.
        await using var prepared = await sandbox.PrepareAsync(BuildId(), BuildInfo(), guard.Token);
        var execution = Stopwatch.StartNew();

        var exception = await Assert.ThrowsAsync<BuildServiceException>(() =>
            prepared.RunAndStageAsync(new OutputCapture()).WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Contains("timed out", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(execution.Elapsed >= TimeSpan.FromMilliseconds(800),
            $"Preparation consumed the worker's execution budget: {execution.Elapsed}");
        var commands = await fakeDocker.ReadCommands();
        var start = $"container start --attach {prepared.WorkerContainer}";
        var remove = $"container rm --force {prepared.WorkerContainer}";
        Assert.Contains(start, commands);
        Assert.True(commands.IndexOf(start) < commands.IndexOf(remove));
        Assert.DoesNotContain(commands, command => command.Contains("pb-stager-", StringComparison.Ordinal));

        await prepared.DisposeAsync();
        await AssertSandboxResourcesCleaned(fakeDocker);
        Assert.True(state.Snapshot.IsReady);
    }

    [Fact]
    public async Task WorkerCreateFailureCleansAllSandboxResources()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create(failWorkerCreate: true);
        var state = ReadyExecutor();
        var exception = await Assert.ThrowsAsync<BuildServiceException>(() =>
            CreateSandbox(fakeDocker, state).PrepareAsync(BuildId(), BuildInfo()));

        Assert.Contains("Creating build container", exception.Message, StringComparison.OrdinalIgnoreCase);
        var commands = await fakeDocker.ReadCommands();
        Assert.Contains(commands, command => command.StartsWith("container create --name pb-worker-", StringComparison.Ordinal));
        Assert.DoesNotContain(commands, command => command.StartsWith("container start --attach pb-worker-", StringComparison.Ordinal));
        await AssertSandboxResourcesCleaned(fakeDocker);
        Assert.False(state.Snapshot.IsReady);
    }

    private static async Task AssertSandboxResourcesCleaned(FakeDocker fakeDocker)
    {
        Assert.Empty(Directory.EnumerateDirectories(fakeDocker.Directory, "pb-build-*"));
        var commands = await fakeDocker.ReadCommands();
        foreach (var prefix in new[] { "pb-worker-", "pb-clone-", "pb-proxy-" })
            Assert.Contains(commands, command => command.StartsWith($"container rm --force {prefix}", StringComparison.Ordinal));
        Assert.Equal(2, commands.Count(command => command.StartsWith("network rm pb-", StringComparison.Ordinal)));
    }

    private DockerBuildSandbox CreateSandbox(FakeDocker fakeDocker, BuildExecutorState state,
        TimeSpan? workerExecutionTimeout = null, bool useRunc = false)
    {
        var processRunner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
        var options = new BuildExecutorOptions
        {
            BuildScratchRoot = fakeDocker.Directory,
            WorkerExecutionTimeout = workerExecutionTimeout ?? TimeSpan.FromMinutes(15),
            UseRunc = useRunc
        };
        return new DockerBuildSandbox(
            _log.CreateLogger<DockerBuildSandbox>(),
            options,
            processRunner,
            state,
            new BuildScratchCleaner(
                NullLogger<BuildScratchCleaner>.Instance,
                processRunner, options));
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
        public void SetWorkerResult(int exitCode)
        {
            Environment.SetEnvironmentVariable("PB_FAKE_WORKER_EXIT_CODE", exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        public static async Task<FakeDocker> Create(
            bool failWorkerRemoval = false,
            bool failProxyReadiness = false,
            bool failCloneStart = false,
            bool ambiguousProxyCreate = false,
            string gitCommit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            string gitCommitDate = "2026-08-27T12:34:56Z",
            string buildDate = "2026-09-04T01:02:03Z",
            string buildHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            string invalidStagedFile = "manifest.json",
            string invalidStagedKind = "",
            bool failStagerRemoval = false,
            string failInspection = "",
            bool failStagerStart = false,
            int workerExitCode = 0,
            bool delayWorkerCreate = false,
            bool failWorkerCreate = false,
            bool stallWorker = false)
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
                            pb-worker-*)
                                if [ "${PB_FAKE_FAIL_WORKER_CREATE:-false}" = "true" ]; then
                                    printf '%s\n' 'simulated worker create failure' >&2
                                    exit 21
                                fi
                                if [ "${PB_FAKE_DELAY_WORKER_CREATE:-false}" = "true" ]; then sleep 2; fi
                                ;;
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
                                create="$(cat "${commands}.create.${target}")"
                                for argument in $create; do
                                    case "$argument" in
                                        type=bind,source=*,target=*)
                                            source="${argument#type=bind,source=}"
                                            source="${source%,target=*}"
                                            case "$source" in
                                                "${commands%/commands}"/pb-build-*/*)
                                                    find "$source" -xdev -mindepth 1 -delete
                                                    ;;
                                                *) exit 3 ;;
                                            esac
                                            ;;
                                    esac
                                done
                                ;;
                            pb-worker-*)
                                printf '%s\n' 'worker log'
                                if [ "${PB_FAKE_STALL_WORKER:-false}" = "true" ]; then sleep 30; fi
                                exit "${PB_FAKE_WORKER_EXIT_CODE:-0}"
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
                                printf '%s\n' "{\"assemblyName\":\"Example\",\"gitCommit\":\"${PB_FAKE_GIT_COMMIT:?}\",\"gitCommitDate\":\"${PB_FAKE_GIT_COMMIT_DATE:?}\",\"buildDate\":\"${PB_FAKE_BUILD_DATE:?}\",\"buildHash\":\"${PB_FAKE_BUILD_HASH-}\",\"gitRepository\":\"https://attacker.invalid/repo\",\"gitRef\":\"attacker\",\"pluginDir\":\"attacker\",\"buildConfig\":\"Debug\"}" > "$staging/build-env.json"
                                printf '%s\n' '{}' > "$staging/manifest.json"
                                printf '%s' 'canonical artifact' > "$staging/artifact.btcpay"
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
                "PB_FAKE_BUILD_HASH",
                "PB_FAKE_INVALID_STAGED_FILE",
                "PB_FAKE_INVALID_STAGED_KIND",
                "PB_FAKE_FAIL_STAGER_REMOVE",
                "PB_FAKE_FAIL_INSPECTION",
                "PB_FAKE_FAIL_STAGER_START",
                "PB_FAKE_WORKER_EXIT_CODE",
                "PB_FAKE_DELAY_WORKER_CREATE",
                "PB_FAKE_FAIL_WORKER_CREATE",
                "PB_FAKE_STALL_WORKER"
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
            Environment.SetEnvironmentVariable("PB_FAKE_BUILD_HASH", buildHash);
            Environment.SetEnvironmentVariable("PB_FAKE_INVALID_STAGED_FILE", invalidStagedFile);
            Environment.SetEnvironmentVariable("PB_FAKE_INVALID_STAGED_KIND", invalidStagedKind);
            Environment.SetEnvironmentVariable("PB_FAKE_FAIL_STAGER_REMOVE", failStagerRemoval ? "true" : "false");
            Environment.SetEnvironmentVariable("PB_FAKE_FAIL_INSPECTION", failInspection);
            Environment.SetEnvironmentVariable("PB_FAKE_FAIL_STAGER_START", failStagerStart ? "true" : "false");
            Environment.SetEnvironmentVariable("PB_FAKE_DELAY_WORKER_CREATE", delayWorkerCreate ? "true" : "false");
            Environment.SetEnvironmentVariable("PB_FAKE_FAIL_WORKER_CREATE", failWorkerCreate ? "true" : "false");
            Environment.SetEnvironmentVariable("PB_FAKE_STALL_WORKER", stallWorker ? "true" : "false");
            var fake = new FakeDocker(directory, originalEnvironment);
            fake.SetWorkerResult(workerExitCode);
            return fake;
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
