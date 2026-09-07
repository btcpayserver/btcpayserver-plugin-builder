using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PluginBuilder.Configuration;
using PluginBuilder.HostedServices;
using PluginBuilder.Services;
using Xunit;

namespace PluginBuilder.Tests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class DockerStartupIsolationTests
{
    private const string WorkerImageId =
        "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string ProxyImageId =
        "sha256:2222222222222222222222222222222222222222222222222222222222222222";

    [Fact]
    public async Task StartupPublishesExactImagesOnlyAfterHardenedRunscSmokeSucceeds()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create(runscAvailable: true);
        var state = new BuildExecutorState();

        await CreateService(fakeDocker, state).StartAsync(CancellationToken.None);

        Assert.Equal(
            new BuildExecutorSnapshot(true, WorkerImageId, ProxyImageId, null),
            state.Snapshot);

        var commands = await fakeDocker.ReadCommands();
        Assert.Contains("build --platform linux/amd64 -f PluginBuilder.Dockerfile -t plugin-builder .", commands);
        Assert.Contains("build --platform linux/amd64 -f PluginBuilder.Proxy.Dockerfile -t plugin-builder-proxy .", commands);
        Assert.DoesNotContain(commands, command => command.Contains("azure", StringComparison.OrdinalIgnoreCase));
        Assert.Contains($"image inspect --format {{{{.Id}}}} plugin-builder", commands);
        Assert.Contains($"image inspect --format {{{{.Id}}}} plugin-builder-proxy", commands);

        var workerSmoke = Assert.Single(
            commands,
            command => command.StartsWith(
                "container create --name plugin-builder-runsc-smoke-",
                StringComparison.Ordinal));
        Assert.Contains("--runtime runsc", workerSmoke, StringComparison.Ordinal);
        Assert.Contains("--network none", workerSmoke, StringComparison.Ordinal);
        Assert.Contains("--read-only", workerSmoke, StringComparison.Ordinal);
        Assert.Contains("--cap-drop ALL", workerSmoke, StringComparison.Ordinal);
        Assert.Contains("--security-opt no-new-privileges:true", workerSmoke, StringComparison.Ordinal);
        Assert.EndsWith(WorkerImageId, workerSmoke, StringComparison.Ordinal);
        Assert.Contains(
            commands,
            command => command.StartsWith(
                "container start --attach plugin-builder-runsc-smoke-",
                StringComparison.Ordinal));
        Assert.Contains(
            commands,
            command => command.StartsWith(
                "container rm --force plugin-builder-runsc-smoke-",
                StringComparison.Ordinal));

        var scratchSmokes = commands.Where(command => command.StartsWith(
                "container create --name plugin-builder-scratch-smoke-",
                StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, scratchSmokes.Length);
        foreach (var slot in new[] { "slot-0", "slot-1" })
        {
            var slotDirectory = Path.Combine(fakeDocker.Directory, slot);
            var scratchSmoke = Assert.Single(scratchSmokes, command => command.Contains(
                $"--mount type=bind,source={slotDirectory},target=/scratch,readonly",
                StringComparison.Ordinal));
            Assert.Contains("--runtime runsc", scratchSmoke, StringComparison.Ordinal);
            Assert.Contains("--network none", scratchSmoke, StringComparison.Ordinal);
            Assert.Contains("--read-only", scratchSmoke, StringComparison.Ordinal);
            Assert.Contains("--user 0:0", scratchSmoke, StringComparison.Ordinal);
            Assert.Contains("--cap-drop ALL", scratchSmoke, StringComparison.Ordinal);
            Assert.Contains("--security-opt no-new-privileges:true", scratchSmoke, StringComparison.Ordinal);
            Assert.Contains("--entrypoint /bin/sh", scratchSmoke, StringComparison.Ordinal);
            Assert.Contains($"{WorkerImageId} -c set -eu;", scratchSmoke, StringComparison.Ordinal);
            Assert.Contains("test -f \"$1\"", scratchSmoke, StringComparison.Ordinal);
            Assert.Contains("stat -f -c %S", scratchSmoke, StringComparison.Ordinal);
            Assert.Contains("stat -f -c %a", scratchSmoke, StringComparison.Ordinal);
            Assert.Contains("stat -f -c %c", scratchSmoke, StringComparison.Ordinal);
            Assert.Contains("stat -f -c %d", scratchSmoke, StringComparison.Ordinal);
            Assert.Contains("17179869184 4294967296 65536 8192", scratchSmoke, StringComparison.Ordinal);
            Assert.Contains("scratch-mount-probe /scratch/pb-mount-probe-", scratchSmoke, StringComparison.Ordinal);
            var smokeName = scratchSmoke.Split(' ', StringSplitOptions.RemoveEmptyEntries)[3];
            Assert.Contains($"container start --attach {smokeName}", commands);
            Assert.Contains($"container rm --force {smokeName}", commands);
            Assert.Empty(Directory.EnumerateFiles(slotDirectory, "pb-mount-probe-*"));
        }

        var proxySmoke = Assert.Single(
            commands,
            command => command.StartsWith(
                "container create --name plugin-builder-proxy-smoke-",
                StringComparison.Ordinal));
        Assert.Contains("--network none", proxySmoke, StringComparison.Ordinal);
        Assert.Contains("--runtime runsc", proxySmoke, StringComparison.Ordinal);
        Assert.Contains("--read-only", proxySmoke, StringComparison.Ordinal);
        Assert.Contains("--user 13:13", proxySmoke, StringComparison.Ordinal);
        Assert.Contains("--cap-drop ALL", proxySmoke, StringComparison.Ordinal);
        Assert.Contains("--security-opt no-new-privileges:true", proxySmoke, StringComparison.Ordinal);
        Assert.Contains("--tmpfs /tmp:", proxySmoke, StringComparison.Ordinal);
        Assert.Contains("--tmpfs /run/squid:", proxySmoke, StringComparison.Ordinal);
        Assert.Contains("--tmpfs /var/log/squid:", proxySmoke, StringComparison.Ordinal);
        Assert.Contains("--tmpfs /var/spool/squid:", proxySmoke, StringComparison.Ordinal);
        Assert.Contains(
            $"{ProxyImageId} -k parse -f /etc/squid/squid.conf",
            proxySmoke,
            StringComparison.Ordinal);
        Assert.Contains(
            commands,
            command => command.StartsWith(
                "container start --attach plugin-builder-proxy-smoke-",
                StringComparison.Ordinal));
        Assert.Contains(
            commands,
            command => command.StartsWith(
                "container rm --force plugin-builder-proxy-smoke-",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task AmbiguousSmokeCreateRetriesRemovalWhenContainerAppearsAfterInitialNotFound()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create(
            runscAvailable: true,
            ambiguousRunscSmokeCreate: true);
        var state = new BuildExecutorState();
        using var cancellation = new CancellationTokenSource();
        var startup = CreateService(fakeDocker, state).StartAsync(cancellation.Token);

        await fakeDocker.WaitForAmbiguousCreate();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup);

        var commands = await fakeDocker.ReadCommands();
        var create = Assert.Single(
            commands,
            command => command.StartsWith(
                "container create --name plugin-builder-runsc-smoke-",
                StringComparison.Ordinal));
        var containerName = create.Split(' ', StringSplitOptions.RemoveEmptyEntries)[3];
        Assert.Equal(
            2,
            commands.Count(command => command == $"container rm --force {containerName}"));
        Assert.False(fakeDocker.AmbiguousContainerExists);
        Assert.DoesNotContain($"container start --attach {containerName}", commands);
        Assert.False(state.Snapshot.IsReady);
        Assert.Equal("Build executor startup was cancelled", state.Snapshot.UnavailableReason);
    }

    [Fact]
    public async Task StopCancelsBuildsAndReconcilesEveryManagedDockerResource()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create(runscAvailable: true);
        var state = new BuildExecutorState();
        var service = CreateService(fakeDocker, state);
        await service.StartAsync(CancellationToken.None);
        var activeToken = state.StopToken;
        Assert.False(activeToken.IsCancellationRequested);

        var startupCommandCount = (await fakeDocker.ReadCommands()).Length;
        await fakeDocker.ExposeManagedResources();
        await service.StopAsync(CancellationToken.None);

        Assert.True(activeToken.IsCancellationRequested);
        Assert.False(state.Snapshot.IsReady);
        Assert.Equal("Build executor shutdown is in progress", state.Snapshot.UnavailableReason);

        var stopCommands = (await fakeDocker.ReadCommands()).Skip(startupCommandCount).ToArray();
        foreach (var listCommand in new[]
                 {
                     $"container ls --all --quiet --filter label={BuildExecutorDocker.ManagedResourceLabel}",
                     $"network ls --quiet --filter label={BuildExecutorDocker.ManagedResourceLabel}",
                     $"volume ls --quiet --filter label={BuildExecutorDocker.ManagedResourceLabel}"
                 })
            Assert.Equal(2, stopCommands.Count(command => command == listCommand));

        Assert.Equal(1, stopCommands.Count(command => command == "container rm --force managed-container"));
        Assert.Equal(1, stopCommands.Count(command => command == "network rm managed-network"));
        Assert.Equal(1, stopCommands.Count(command => command == "volume rm --force managed-volume"));
    }

    [Fact]
    public async Task MissingRunscKeepsExecutorUnavailableAndDoesNotCreateSmokeContainer()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create(runscAvailable: false);
        var state = new BuildExecutorState();

        await CreateService(fakeDocker, state).StartAsync(CancellationToken.None);

        Assert.False(state.Snapshot.IsReady);
        Assert.Null(state.Snapshot.WorkerImageId);
        Assert.Null(state.Snapshot.ProxyImageId);
        Assert.Contains("runsc", state.Snapshot.UnavailableReason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            await fakeDocker.ReadCommands(),
            command => command.StartsWith("container create ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("slot-0")]
    [InlineData("slot-1")]
    public async Task StartupReconcilesManagedScratchThroughTrustedWorkerBeforeBecomingReady(string slot)
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create(runscAvailable: true);
        var staleScratch = Path.Combine(fakeDocker.Directory, slot, $"pb-build-{new string('a', 32)}");
        Directory.CreateDirectory(Path.Combine(staleScratch, "source"));
        Directory.CreateDirectory(Path.Combine(staleScratch, "work"));
        Directory.CreateDirectory(Path.Combine(staleScratch, "output"));
        Directory.CreateDirectory(Path.Combine(staleScratch, "staging"));
        var state = new BuildExecutorState();

        await CreateService(fakeDocker, state).StartAsync(CancellationToken.None);

        Assert.True(state.Snapshot.IsReady);
        Assert.False(Directory.Exists(staleScratch));
        var commands = await fakeDocker.ReadCommands();
        var cleanupCreate = Assert.Single(
            commands,
            command => command.StartsWith("container create --name pb-scratch-clean-", StringComparison.Ordinal));
        Assert.Contains("--runtime runsc", cleanupCreate, StringComparison.Ordinal);
        Assert.Contains("--network none", cleanupCreate, StringComparison.Ordinal);
        Assert.Contains($"--entrypoint /usr/bin/find {WorkerImageId}", cleanupCreate, StringComparison.Ordinal);
        var cleanupRemove = Assert.Single(
            commands,
            command => command.StartsWith("container rm --force pb-scratch-clean-", StringComparison.Ordinal));
        var proxySmoke = Assert.Single(
            commands,
            command => command.StartsWith("container create --name plugin-builder-proxy-smoke-", StringComparison.Ordinal));
        Assert.True(commands.IndexOf(cleanupCreate) < commands.IndexOf(cleanupRemove));
        Assert.True(commands.IndexOf(cleanupRemove) < commands.IndexOf(proxySmoke));
    }

    [Fact]
    public async Task MutableOrMalformedImageReferenceKeepsExecutorUnavailable()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create(
            runscAvailable: true,
            invalidWorkerImageId: true);
        var state = new BuildExecutorState();

        await CreateService(fakeDocker, state).StartAsync(CancellationToken.None);

        Assert.False(state.Snapshot.IsReady);
        Assert.Null(state.Snapshot.WorkerImageId);
        Assert.Null(state.Snapshot.ProxyImageId);
        Assert.Contains("immutable image ID", state.Snapshot.UnavailableReason, StringComparison.Ordinal);
        Assert.DoesNotContain(
            await fakeDocker.ReadCommands(),
            command => command.StartsWith("container create ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CleanupFailureKeepsExecutorUnavailableAndDoesNotBuildImages()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create(
            runscAvailable: true,
            staleContainer: true,
            failStaleContainerRemoval: true);
        var state = new BuildExecutorState();

        await CreateService(fakeDocker, state).StartAsync(CancellationToken.None);

        Assert.False(state.Snapshot.IsReady);
        Assert.Contains("remove", state.Snapshot.UnavailableReason, StringComparison.OrdinalIgnoreCase);
        var commands = await fakeDocker.ReadCommands();
        Assert.Contains(
            commands,
            command => command == "container rm --force stale-build-container");
        Assert.DoesNotContain(
            commands,
            command => command.StartsWith("build ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SkipBuildStillReconcilesAndRemovesManagedResourcesBeforeReturning()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create(
            runscAvailable: true,
            staleContainer: true,
            skipBuild: true);
        var state = new BuildExecutorState();

        await CreateService(fakeDocker, state).StartAsync(CancellationToken.None);

        Assert.False(state.Snapshot.IsReady);
        Assert.Equal("DOCKER_STARTUP_SKIP_BUILD=true", state.Snapshot.UnavailableReason);
        Assert.Equal(
            [
                $"container ls --all --quiet --filter label={BuildExecutorDocker.ManagedResourceLabel}",
                "container rm --force stale-build-container",
                $"network ls --quiet --filter label={BuildExecutorDocker.ManagedResourceLabel}",
                $"volume ls --quiet --filter label={BuildExecutorDocker.ManagedResourceLabel}"
            ],
            await fakeDocker.ReadCommands());
    }

    [Fact]
    public async Task StalledReconciliationKeepsExecutorUnavailableWithoutBlockingStartup()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create(
            runscAvailable: true,
            skipBuild: true,
            stallContainerList: true);
        var state = new BuildExecutorState();
        // The real 30-second operation deadline must fire before this test guard.
        // Without the local deadline startup would only finish by cancelling the host.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(40));

        await CreateService(fakeDocker, state).StartAsync(guard.Token);

        Assert.False(guard.IsCancellationRequested);
        Assert.False(state.Snapshot.IsReady);
        Assert.Equal("Docker startup operation timed out.", state.Snapshot.UnavailableReason);
        Assert.Equal(
            [
                $"container ls --all --quiet --filter label={BuildExecutorDocker.ManagedResourceLabel}",
                $"network ls --quiet --filter label={BuildExecutorDocker.ManagedResourceLabel}",
                $"volume ls --quiet --filter label={BuildExecutorDocker.ManagedResourceLabel}"
            ],
            await fakeDocker.ReadCommands());
    }

    [Fact]
    public async Task HostCancellationDuringReconciliationIsNotTreatedAsAnExecutorTimeout()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create(runscAvailable: true, skipBuild: true);
        var state = new BuildExecutorState();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateService(fakeDocker, state).StartAsync(cancellation.Token));

        Assert.False(state.Snapshot.IsReady);
        Assert.Equal("Build executor startup was cancelled", state.Snapshot.UnavailableReason);
        Assert.Empty(await fakeDocker.ReadCommands());
    }

    [Fact]
    public async Task InvalidFilesystemRootStillReconcilesManagedResourcesBeforePreflight()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create(
            runscAvailable: true,
            staleContainer: true);
        var state = new BuildExecutorState();
        var root = Path.GetPathRoot(fakeDocker.Directory)!;

        await CreateService(fakeDocker, state, root).StartAsync(CancellationToken.None);

        Assert.False(state.Snapshot.IsReady);
        Assert.Contains("BUILD_SCRATCH_ROOT", state.Snapshot.UnavailableReason, StringComparison.Ordinal);
        Assert.Equal(
            [
                $"container ls --all --quiet --filter label={BuildExecutorDocker.ManagedResourceLabel}",
                "container rm --force stale-build-container",
                $"network ls --quiet --filter label={BuildExecutorDocker.ManagedResourceLabel}",
                $"volume ls --quiet --filter label={BuildExecutorDocker.ManagedResourceLabel}"
            ],
            await fakeDocker.ReadCommands());
    }

    [LinuxFact]
    public async Task NonMountpointScratchIsRejectedBeforeDeviceStat()
    {
        await using var fakeDocker = await FakeDocker.Create(runscAvailable: true);
        var state = new BuildExecutorState();
        var nonexistentDataDirectory = Path.Combine(fakeDocker.Directory, "missing-data");

        await CreateService(
                fakeDocker,
                state,
                bypassDedicatedFilesystemProbe: false,
                dataDir: nonexistentDataDirectory)
            .StartAsync(CancellationToken.None);

        Assert.False(state.Snapshot.IsReady);
        Assert.Contains("dedicated mount point", state.Snapshot.UnavailableReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            [
                $"container ls --all --quiet --filter label={BuildExecutorDocker.ManagedResourceLabel}",
                $"network ls --quiet --filter label={BuildExecutorDocker.ManagedResourceLabel}",
                $"volume ls --quiet --filter label={BuildExecutorDocker.ManagedResourceLabel}"
            ],
            await fakeDocker.ReadCommands());
    }

    [Theory]
    [Trait("Category", "ExecutorIntegration")]
    [InlineData("slots")]
    [InlineData("slot-0")]
    [InlineData("slot-1")]
    [InlineData("distinct")]
    public async Task ScratchFilesystemProbeRequiresDistinctSlotAndApplicationDevices(string overlap)
    {
        Assert.True(OperatingSystem.IsLinux(), "This integration test requires a Linux host with mount privileges.");

        await using var fakeDocker = await FakeDocker.Create(runscAvailable: true);
        var processRunner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
        var slots = new[] { Path.Combine(fakeDocker.Directory, "slot-0"), Path.Combine(fakeDocker.Directory, "slot-1") };
        var mounted = new List<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            for (var index = 0; index < slots.Length; index++)
            {
                // Tiny disposable mounts exercise actual mountpoint/stat results;
                // no disk exhaustion or production scratch mount is involved.
                string[] arguments = index == 1 && overlap == "slots"
                    ? ["--bind", slots[0], slots[1]]
                    : ["-t", "tmpfs", "-o", "size=1m,nosuid,nodev,noexec", "tmpfs", slots[index]];
                var mountError = new OutputCapture();
                var mountResult = await processRunner.RunAsync(new ProcessSpec
                {
                    Executable = "/usr/bin/mount",
                    Arguments = arguments,
                    ErrorCapture = mountError
                }, timeout.Token);
                Assert.True(mountResult == 0, $"Could not mount disposable scratch fixture: {mountError}");
                mounted.Add(slots[index]);
            }

            var dataDir = overlap switch
            {
                "slot-0" => slots[0],
                "slot-1" => slots[1],
                _ => fakeDocker.Directory
            };
            var state = new BuildExecutorState();
            if (overlap == "distinct")
            {
                var service = (TestDockerStartupHostedService)CreateService(fakeDocker, state, dataDir: dataDir);
                await service.ProbeScratchFilesystems(timeout.Token);
            }
            else
            {
                await CreateService(fakeDocker, state, bypassDedicatedFilesystemProbe: false, dataDir: dataDir)
                    .StartAsync(timeout.Token);

                Assert.False(state.Snapshot.IsReady);
                Assert.Equal("Build scratch slots must use different filesystems, separate from application data",
                    state.Snapshot.UnavailableReason);
                var commands = await fakeDocker.ReadCommands();
                Assert.DoesNotContain(commands, command => command.StartsWith("build ", StringComparison.Ordinal));
                Assert.DoesNotContain(commands, command => command.StartsWith("container create ", StringComparison.Ordinal));
            }
        }
        finally
        {
            var cleanupErrors = new List<string>();
            foreach (var slot in mounted.AsEnumerable().Reverse())
            {
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var error = new OutputCapture();
                try
                {
                    var result = await processRunner.RunAsync(new ProcessSpec
                    {
                        Executable = "/usr/bin/umount",
                        Arguments = ["--", slot],
                        ErrorCapture = error
                    }, cleanupTimeout.Token);
                    if (result != 0)
                        cleanupErrors.Add($"Could not unmount {slot}: {error}");
                }
                catch (OperationCanceledException)
                {
                    cleanupErrors.Add($"Timed out unmounting {slot}");
                }
            }
            Assert.Empty(cleanupErrors);
        }
    }

    [Fact]
    public async Task SymbolicLinkScratchDirectoryKeepsExecutorUnavailableAfterReconciliation()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create(runscAvailable: true);
        var target = Path.Combine(fakeDocker.Directory, "scratch-target");
        var link = Path.Combine(fakeDocker.Directory, "scratch-link");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(link, target);
        var state = new BuildExecutorState();

        await CreateService(fakeDocker, state, link).StartAsync(CancellationToken.None);

        Assert.False(state.Snapshot.IsReady);
        Assert.Contains("symbolic link", state.Snapshot.UnavailableReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            [
                $"container ls --all --quiet --filter label={BuildExecutorDocker.ManagedResourceLabel}",
                $"network ls --quiet --filter label={BuildExecutorDocker.ManagedResourceLabel}",
                $"volume ls --quiet --filter label={BuildExecutorDocker.ManagedResourceLabel}"
            ],
            await fakeDocker.ReadCommands());
    }

    [Theory]
    [InlineData("slot-0")]
    [InlineData("slot-1")]
    public async Task MissingScratchSlotKeepsExecutorUnavailableBeforeBuildingImages(string slot)
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create(runscAvailable: true);
        Directory.Delete(Path.Combine(fakeDocker.Directory, slot));
        var state = new BuildExecutorState();

        await CreateService(fakeDocker, state).StartAsync(CancellationToken.None);

        Assert.False(state.Snapshot.IsReady);
        Assert.Contains("scratch slot must exist", state.Snapshot.UnavailableReason, StringComparison.Ordinal);
        var commands = await fakeDocker.ReadCommands();
        Assert.DoesNotContain(commands, command => command.StartsWith("build ", StringComparison.Ordinal));
        Assert.DoesNotContain(commands, command => command.StartsWith("container create ", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(fakeDocker.Directory, slot)));
    }

    [Theory]
    [InlineData("slot-0")]
    [InlineData("slot-1")]
    public async Task SymbolicLinkScratchSlotKeepsExecutorUnavailableBeforeBuildingImages(string slot)
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var fakeDocker = await FakeDocker.Create(runscAvailable: true);
        var slotDirectory = Path.Combine(fakeDocker.Directory, slot);
        var target = Path.Combine(fakeDocker.Directory, "slot-target");
        Directory.CreateDirectory(target);
        Directory.Delete(slotDirectory);
        Directory.CreateSymbolicLink(slotDirectory, target);
        var state = new BuildExecutorState();

        await CreateService(fakeDocker, state).StartAsync(CancellationToken.None);

        Assert.False(state.Snapshot.IsReady);
        Assert.Contains("symbolic link", state.Snapshot.UnavailableReason, StringComparison.OrdinalIgnoreCase);
        var commands = await fakeDocker.ReadCommands();
        Assert.DoesNotContain(commands, command => command.StartsWith("build ", StringComparison.Ordinal));
        Assert.DoesNotContain(commands, command => command.StartsWith("container create ", StringComparison.Ordinal));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    private static DockerStartupHostedService CreateService(
        FakeDocker fakeDocker,
        BuildExecutorState state,
        string? scratchRoot = null,
        bool bypassDedicatedFilesystemProbe = true,
        string? dataDir = null)
    {
        var processRunner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
        var options = new PluginBuilderOptions
        {
            DataDir = dataDir ?? fakeDocker.Directory,
            BuildScratchRoot = scratchRoot ?? fakeDocker.Directory
        };
        if (!bypassDedicatedFilesystemProbe)
            return new DockerStartupHostedService(
                NullLogger<DockerStartupHostedService>.Instance,
                new TestWebHostEnvironment { ContentRootPath = fakeDocker.Directory },
                processRunner,
                state,
                new BuildScratchCleaner(
                    NullLogger<BuildScratchCleaner>.Instance,
                    processRunner),
                options);

        return new TestDockerStartupHostedService(
            NullLogger<DockerStartupHostedService>.Instance,
            new TestWebHostEnvironment { ContentRootPath = fakeDocker.Directory },
            processRunner,
            state,
            new BuildScratchCleaner(
                NullLogger<BuildScratchCleaner>.Instance,
                processRunner),
            options);
    }

    private sealed class TestDockerStartupHostedService(
        ILogger<DockerStartupHostedService> logger,
        IWebHostEnvironment environment,
        ProcessRunner processRunner,
        BuildExecutorState executorState,
        BuildScratchCleaner scratchCleaner,
        PluginBuilderOptions options)
        : DockerStartupHostedService(
            logger,
            environment,
            processRunner,
            executorState,
            scratchCleaner,
            options)
    {
        public Task ProbeScratchFilesystems(CancellationToken cancellationToken) =>
            base.RequireDedicatedScratchFilesystem(cancellationToken);

        protected override Task RequireDedicatedScratchFilesystem(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDocker : IAsyncDisposable
    {
        private readonly Dictionary<string, string?> _originalEnvironment;

        private FakeDocker(string directory, Dictionary<string, string?> originalEnvironment)
        {
            Directory = directory;
            _originalEnvironment = originalEnvironment;
        }

        public string Directory { get; }

        public static async Task<FakeDocker> Create(
            bool runscAvailable,
            bool staleContainer = false,
            bool failStaleContainerRemoval = false,
            bool skipBuild = false,
            bool invalidWorkerImageId = false,
            bool ambiguousRunscSmokeCreate = false,
            bool stallContainerList = false)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"plugin-builder-startup-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(directory);
            System.IO.Directory.CreateDirectory(Path.Combine(directory, "slot-0"));
            System.IO.Directory.CreateDirectory(Path.Combine(directory, "slot-1"));
            var dockerPath = Path.Combine(directory, "docker");
            await File.WriteAllTextAsync(dockerPath, """
                #!/bin/sh
                set -eu
                commands="${PB_FAKE_DOCKER_COMMANDS:?}"
                printf '%s\n' "$*" >> "$commands"

                case "$1:$2" in
                    container:ls)
                        if [ "${PB_FAKE_STALL_CONTAINER_LIST:-false}" = "true" ]; then
                            sleep 60
                        fi
                        if [ "${PB_FAKE_STALE_CONTAINER:-false}" = "true" ]; then
                            printf '%s\n' stale-build-container
                        fi
                        if [ -f "${commands}.managed-container" ]; then
                            printf '%s\n' managed-container
                        fi
                        ;;
                    network:ls)
                        if [ -f "${commands}.managed-network" ]; then
                            printf '%s\n' managed-network
                        fi
                        ;;
                    volume:ls)
                        if [ -f "${commands}.managed-volume" ]; then
                            printf '%s\n' managed-volume
                        fi
                        ;;
                    container:rm)
                        target=""
                        for argument in "$@"; do target="$argument"; done
                        case "$target" in
                            plugin-builder-runsc-smoke-*)
                                if [ "${PB_FAKE_AMBIGUOUS_RUNSC_CREATE:-false}" = "true" ]; then
                                    if [ ! -f "${commands}.ambiguous-remove-attempted" ]; then
                                        : > "${commands}.ambiguous-remove-attempted"
                                        : > "${commands}.ambiguous-container-exists"
                                        printf '%s\n' "Error response from daemon: No such container: $target" >&2
                                        exit 1
                                    fi
                                    rm -f "${commands}.ambiguous-container-exists"
                                fi
                                ;;
                        esac
                        if [ "$target" = "stale-build-container" ] &&
                           [ "${PB_FAKE_REMOVE_FAIL:-false}" = "true" ]; then
                            exit 12
                        fi
                        if [ "$target" = "managed-container" ]; then
                            rm -f "${commands}.managed-container"
                        fi
                        ;;
                    network:rm)
                        if [ "${3:-}" = "managed-network" ]; then
                            rm -f "${commands}.managed-network"
                        fi
                        ;;
                    volume:rm)
                        target=""
                        for argument in "$@"; do target="$argument"; done
                        if [ "$target" = "managed-volume" ]; then
                            rm -f "${commands}.managed-volume"
                        fi
                        ;;
                    build:--platform)
                        ;;
                    image:inspect)
                        target=""
                        for argument in "$@"; do target="$argument"; done
                        case "$target" in
                            plugin-builder) printf '%s\n' "${PB_FAKE_WORKER_IMAGE_ID:?}" ;;
                            plugin-builder-proxy) printf '%s\n' "${PB_FAKE_PROXY_IMAGE_ID:?}" ;;
                            *) exit 2 ;;
                        esac
                        ;;
                    info:--format)
                        if [ "${PB_FAKE_RUNSC:-false}" = "true" ]; then
                            printf '%s\n' runsc
                        fi
                        ;;
                    container:create)
                        previous=""
                        name=""
                        for argument in "$@"; do
                            if [ "$previous" = "--name" ]; then name="$argument"; break; fi
                            previous="$argument"
                        done
                        case "$name" in
                            plugin-builder-runsc-smoke-*)
                                if [ "${PB_FAKE_AMBIGUOUS_RUNSC_CREATE:-false}" = "true" ]; then
                                    : > "${commands}.ambiguous-create-started"
                                    sleep 60
                                fi
                                ;;
                        esac
                        ;;
                    container:start)
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
                "DOCKER_STARTUP_SKIP_BUILD",
                "PB_FAKE_DOCKER_COMMANDS",
                "PB_FAKE_RUNSC",
                "PB_FAKE_WORKER_IMAGE_ID",
                "PB_FAKE_PROXY_IMAGE_ID",
                "PB_FAKE_STALE_CONTAINER",
                "PB_FAKE_REMOVE_FAIL",
                "PB_FAKE_AMBIGUOUS_RUNSC_CREATE",
                "PB_FAKE_STALL_CONTAINER_LIST"
            ];
            var originalEnvironment = keys.ToDictionary(
                key => key,
                Environment.GetEnvironmentVariable);

            Environment.SetEnvironmentVariable(
                "PATH",
                directory + Path.PathSeparator + originalEnvironment["PATH"]);
            Environment.SetEnvironmentVariable(
                "DOCKER_STARTUP_SKIP_BUILD",
                skipBuild ? "true" : null);
            Environment.SetEnvironmentVariable(
                "PB_FAKE_DOCKER_COMMANDS",
                Path.Combine(directory, "commands"));
            Environment.SetEnvironmentVariable(
                "PB_FAKE_RUNSC",
                runscAvailable ? "true" : "false");
            Environment.SetEnvironmentVariable(
                "PB_FAKE_WORKER_IMAGE_ID",
                invalidWorkerImageId ? "plugin-builder:latest" : WorkerImageId);
            Environment.SetEnvironmentVariable("PB_FAKE_PROXY_IMAGE_ID", ProxyImageId);
            Environment.SetEnvironmentVariable(
                "PB_FAKE_STALE_CONTAINER",
                staleContainer ? "true" : "false");
            Environment.SetEnvironmentVariable(
                "PB_FAKE_REMOVE_FAIL",
                failStaleContainerRemoval ? "true" : "false");
            Environment.SetEnvironmentVariable(
                "PB_FAKE_AMBIGUOUS_RUNSC_CREATE",
                ambiguousRunscSmokeCreate ? "true" : "false");
            Environment.SetEnvironmentVariable(
                "PB_FAKE_STALL_CONTAINER_LIST",
                stallContainerList ? "true" : "false");

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

        public async Task<string[]> ReadCommands()
        {
            var path = Path.Combine(Directory, "commands");
            return File.Exists(path) ? await File.ReadAllLinesAsync(path) : [];
        }

        public async Task ExposeManagedResources()
        {
            var commands = Path.Combine(Directory, "commands");
            await File.WriteAllTextAsync(commands + ".managed-container", string.Empty);
            await File.WriteAllTextAsync(commands + ".managed-network", string.Empty);
            await File.WriteAllTextAsync(commands + ".managed-volume", string.Empty);
        }

        public ValueTask DisposeAsync()
        {
            foreach (var (key, value) in _originalEnvironment)
                Environment.SetEnvironmentVariable(key, value);

            System.IO.Directory.Delete(Directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = nameof(DockerStartupIsolationTests);
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
