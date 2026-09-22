using Microsoft.Extensions.Logging.Abstractions;
using PluginBuilder.BuildBroker.Configuration;
using Xunit;

using PluginBuilder.BuildBroker;
using PluginBuilder.BuildBroker.HostedServices;
using PluginBuilder.BuildBroker.Services;
using PluginBuilder.Builds.Services;

namespace PluginBuilder.Tests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class DockerStartupIsolationTests
{
    private const string WorkerImageId =
        "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string ProxyImageId =
        "sha256:2222222222222222222222222222222222222222222222222222222222222222";
    private const string WorkerImage = "btcpayserver/btcpayserver-plugin-builder-worker:v1.0.76";
    private const string ProxyImage = "btcpayserver/btcpayserver-plugin-builder-proxy:v1.0.76";

    [UnixTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-rc.1")]
    public async Task StartupMarksExecutorReadyWithResolvedImageIdsAfterIsolationChecks(string? releaseSuffix)
    {
        await using var fakeDocker = await FakeDocker.Create(runscAvailable: true);
        var state = new BuildExecutorState();
        var worker = releaseSuffix is null ? WorkerImageId : WorkerImage + releaseSuffix;
        var proxy = releaseSuffix is null ? ProxyImageId : ProxyImage + releaseSuffix;
        var service = CreateService(fakeDocker, state, workerImage: worker, proxyImage: proxy);

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(
            new BuildExecutorSnapshot(true, WorkerImageId, ProxyImageId, null),
            state.Snapshot);

        var commands = await fakeDocker.ReadCommands();
        // Startup only resolves prebuilt, pinned images; it never builds one.
        Assert.DoesNotContain(commands, command => command.StartsWith("build "));
        Assert.Contains($"image inspect --format {{{{.Id}}}} {worker}", commands);
        Assert.Contains($"image inspect --format {{{{.Id}}}} {proxy}", commands);
        if (releaseSuffix is null)
            Assert.DoesNotContain(commands, command => command.StartsWith("pull "));
        else
        {
            Assert.Single(commands, command => command == $"pull --platform linux/amd64 {worker}");
            Assert.Single(commands, command => command == $"pull --platform linux/amd64 {proxy}");
            Assert.True(commands.IndexOf($"volume ls --quiet --filter label={BuildExecutorDocker.ManagedResourceLabel}") <
                        commands.IndexOf($"pull --platform linux/amd64 {worker}"));
        }

        var workerSmoke = Assert.Single(
            commands,
            command => command.StartsWith(
                "container create --name plugin-builder-runtime-smoke-",
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
                "container start --attach plugin-builder-runtime-smoke-",
                StringComparison.Ordinal));
        Assert.Contains(
            commands,
            command => command.StartsWith(
                "container rm --force plugin-builder-runtime-smoke-",
                StringComparison.Ordinal));

        var scratchSmoke = Assert.Single(commands, command => command.StartsWith(
                "container create --name plugin-builder-scratch-smoke-",
                StringComparison.Ordinal));
        Assert.Contains($"--mount type=bind,source={fakeDocker.Directory},target=/scratch,readonly",
            scratchSmoke, StringComparison.Ordinal);
        Assert.Contains("--runtime runsc", scratchSmoke, StringComparison.Ordinal);
        Assert.Contains("--network none", scratchSmoke, StringComparison.Ordinal);
        Assert.Contains("--read-only", scratchSmoke, StringComparison.Ordinal);
        Assert.Contains("--user 0:0", scratchSmoke, StringComparison.Ordinal);
        Assert.Contains("--cap-drop ALL", scratchSmoke, StringComparison.Ordinal);
        Assert.Contains("--security-opt no-new-privileges:true", scratchSmoke, StringComparison.Ordinal);
        Assert.Contains("--entrypoint /bin/sh", scratchSmoke, StringComparison.Ordinal);
        Assert.Contains($"{WorkerImageId} -c set -eu;", scratchSmoke, StringComparison.Ordinal);
        Assert.Contains("scratch-mount-probe /scratch/pb-mount-probe-", scratchSmoke, StringComparison.Ordinal);
        var smokeName = scratchSmoke.Split(' ', StringSplitOptions.RemoveEmptyEntries)[3];
        Assert.Contains($"container start --attach {smokeName}", commands);
        Assert.Contains($"container rm --force {smokeName}", commands);
        Assert.Empty(Directory.EnumerateFiles(fakeDocker.Directory, "pb-mount-probe-*"));

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

        if (releaseSuffix is not null)
        {
            // Even cached release tags must be refreshed on the next startup.
            await service.StartAsync(CancellationToken.None);
            Assert.True(state.Snapshot.IsReady);
            commands = await fakeDocker.ReadCommands();
            Assert.Equal(2, commands.Count(command => command == $"pull --platform linux/amd64 {worker}"));
            Assert.Equal(2, commands.Count(command => command == $"pull --platform linux/amd64 {proxy}"));
        }
    }

    [UnixTheory]
    [InlineData("missing")]
    [InlineData("mismatched")]
    public async Task ScratchMountProbeFailsClosedIfDockerDoesNotSeeTheSameMarker(string markerFailure)
    {
        await using var docker = await FakeDocker.Create(runscAvailable: true, scratchMarkerFailure: markerFailure);
        var state = new BuildExecutorState();

        await CreateService(docker, state).StartAsync(CancellationToken.None);

        Assert.False(state.Snapshot.IsReady);
        Assert.Equal("The build scratch directory is not shared with the Docker host", state.Snapshot.UnavailableReason);
        Assert.Null(state.Snapshot.WorkerImageId);
        Assert.Null(state.Snapshot.ProxyImageId);
        var commands = await docker.ReadCommands();
        var probe = Assert.Single(commands, command =>
            command.StartsWith("container create --name plugin-builder-scratch-smoke-", StringComparison.Ordinal));
        var container = probe.Split(' ', StringSplitOptions.RemoveEmptyEntries)[3];
        Assert.Contains($"container start --attach {container}", commands);
        Assert.Contains($"container rm --force {container}", commands);
        Assert.Empty(Directory.EnumerateFiles(docker.Directory, "pb-mount-probe-*"));
    }

    [UnixFact]
    public async Task AmbiguousSmokeCreateRetriesRemovalWhenContainerAppearsAfterInitialNotFound()
    {
        await using var fakeDocker = await FakeDocker.Create(
            runscAvailable: true,
            ambiguousRunscSmokeCreate: true);
        var state = new BuildExecutorState();
        using var cancellation = new CancellationTokenSource();
        var startup = CreateService(fakeDocker, state).StartAsync(cancellation.Token);

        await fakeDocker.WaitForMarker("ambiguous-create-started");
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup);

        var commands = await fakeDocker.ReadCommands();
        var create = Assert.Single(
            commands,
            command => command.StartsWith(
                "container create --name plugin-builder-runtime-smoke-",
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

    [UnixFact]
    public async Task StopCancelsBuildsAndReconcilesEveryManagedDockerResource()
    {
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

    [UnixFact]
    public async Task MissingRunscKeepsExecutorUnavailableAndDoesNotCreateSmokeContainer()
    {
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

    [UnixFact]
    public async Task StartupReconcilesManagedScratchThroughTrustedWorkerBeforeBecomingReady()
    {
        await using var fakeDocker = await FakeDocker.Create(runscAvailable: true);
        var staleScratch = Path.Combine(fakeDocker.Directory, $"pb-build-{new string('a', 32)}");
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

    [UnixTheory]
    [InlineData("pull-worker")]
    [InlineData("pull-proxy")]
    [InlineData("inspect-worker")]
    [InlineData("inspect-proxy")]
    [InlineData("invalid-worker")]
    [InlineData("invalid-proxy")]
    public async Task ImagePreparationFailureKeepsExecutorUnavailableWithoutCachedFallback(string imageFailure)
    {
        await using var fakeDocker = await FakeDocker.Create(
            runscAvailable: true,
            imageFailure: imageFailure);
        var state = new BuildExecutorState();

        await CreateService(fakeDocker, state, workerImage: WorkerImage, proxyImage: ProxyImage)
            .StartAsync(CancellationToken.None);

        Assert.False(state.Snapshot.IsReady);
        Assert.Null(state.Snapshot.WorkerImageId);
        Assert.Null(state.Snapshot.ProxyImageId);
        Assert.False(string.IsNullOrEmpty(state.Snapshot.UnavailableReason));
        var commands = await fakeDocker.ReadCommands();
        var failedImage = imageFailure.EndsWith("worker", StringComparison.Ordinal) ? WorkerImage : ProxyImage;
        var failedCommand = imageFailure.StartsWith("pull-", StringComparison.Ordinal)
            ? $"pull --platform linux/amd64 {failedImage}"
            : $"image inspect --format {{{{.Id}}}} {failedImage}";
        Assert.Equal(failedCommand, commands[^1]);
        Assert.DoesNotContain(commands, command => command.StartsWith("container create ", StringComparison.Ordinal));
    }

    [UnixTheory]
    [InlineData("mismatch-worker")]
    [InlineData("mismatch-proxy")]
    public async Task LocalImageIdMustResolveToItsExactConfiguredIdentity(string imageFailure)
    {
        await using var docker = await FakeDocker.Create(runscAvailable: true, imageFailure: imageFailure);
        var state = new BuildExecutorState();

        await CreateService(docker, state).StartAsync(CancellationToken.None);

        Assert.False(state.Snapshot.IsReady);
        Assert.Null(state.Snapshot.WorkerImageId);
        Assert.Null(state.Snapshot.ProxyImageId);
        Assert.Contains("identity", state.Snapshot.UnavailableReason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(await docker.ReadCommands(), command =>
            command.StartsWith("pull ") || command.StartsWith("container create "));
    }

    [UnixFact]
    public async Task HostCancellationDuringImagePullStopsPreparationWithoutCachedFallback()
    {
        await using var docker = await FakeDocker.Create(runscAvailable: true, imageFailure: "stall-worker");
        var state = new BuildExecutorState();
        using var cancellation = new CancellationTokenSource();
        var startup = CreateService(docker, state, workerImage: WorkerImage, proxyImage: ProxyImage)
            .StartAsync(cancellation.Token);

        await docker.WaitForMarker("pull-started");
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.False(state.Snapshot.IsReady);
        Assert.Equal("Build executor startup was cancelled", state.Snapshot.UnavailableReason);
        Assert.Equal($"pull --platform linux/amd64 {WorkerImage}", (await docker.ReadCommands())[^1]);
    }

    [UnixFact]
    public async Task CleanupFailureKeepsExecutorUnavailableBeforeInspectingImages()
    {
        await using var fakeDocker = await FakeDocker.Create(
            runscAvailable: true,
            staleContainer: true,
            failStaleContainerRemoval: true);
        var state = new BuildExecutorState();

        await CreateService(fakeDocker, state, workerImage: WorkerImage, proxyImage: ProxyImage)
            .StartAsync(CancellationToken.None);

        Assert.False(state.Snapshot.IsReady);
        Assert.Contains("remove", state.Snapshot.UnavailableReason, StringComparison.OrdinalIgnoreCase);
        var commands = await fakeDocker.ReadCommands();
        Assert.Contains(
            commands,
            command => command == "container rm --force stale-build-container");
        Assert.DoesNotContain(commands, command => command.StartsWith("image inspect ", StringComparison.Ordinal));
        Assert.DoesNotContain(commands, command => command.StartsWith("pull ", StringComparison.Ordinal));
    }

    [UnixFact]
    public async Task DisabledPluginBuildsStillReconcileAndRemoveManagedResourcesBeforeReturning()
    {
        await using var fakeDocker = await FakeDocker.Create(
            runscAvailable: true,
            staleContainer: true,
            disablePluginBuilds: true);
        var state = new BuildExecutorState();

        await CreateService(fakeDocker, state).StartAsync(CancellationToken.None);

        Assert.False(state.Snapshot.IsReady);
        Assert.Equal("PBB_DISABLE_PLUGIN_BUILDS=true", state.Snapshot.UnavailableReason);
        Assert.Equal(
            [
                $"container ls --all --quiet --filter label={BuildExecutorDocker.ManagedResourceLabel}",
                "container rm --force stale-build-container",
                $"network ls --quiet --filter label={BuildExecutorDocker.ManagedResourceLabel}",
                $"volume ls --quiet --filter label={BuildExecutorDocker.ManagedResourceLabel}"
            ],
            await fakeDocker.ReadCommands());
    }

    [UnixFact]
    public async Task StalledReconciliationKeepsExecutorUnavailableWithoutBlockingStartup()
    {
        await using var fakeDocker = await FakeDocker.Create(
            runscAvailable: true,
            disablePluginBuilds: true,
            stallContainerList: true);
        var state = new BuildExecutorState();
        // The operation deadline must fire before this test guard. Without the
        // local deadline startup would only finish by cancelling the host.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await CreateService(fakeDocker, state, dockerOperationTimeout: TimeSpan.FromSeconds(1)).StartAsync(guard.Token);

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

    [UnixFact]
    public async Task HostCancellationDuringReconciliationIsNotTreatedAsAnExecutorTimeout()
    {
        await using var fakeDocker = await FakeDocker.Create(runscAvailable: true, disablePluginBuilds: true);
        var state = new BuildExecutorState();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateService(fakeDocker, state).StartAsync(cancellation.Token));

        Assert.False(state.Snapshot.IsReady);
        Assert.Equal("Build executor startup was cancelled", state.Snapshot.UnavailableReason);
        Assert.Empty(await fakeDocker.ReadCommands());
    }

    [UnixTheory]
    [InlineData("filesystem-root", "cannot be a filesystem root")]
    [InlineData("symbolic-link", "symbolic link")]
    [InlineData("missing", "BUILD_SCRATCH_ROOT does not exist")]
    public async Task InvalidScratchRootKeepsExecutorUnavailableAfterReconciliation(string kind, string expectedReason)
    {
        await using var fakeDocker = await FakeDocker.Create(runscAvailable: true, staleContainer: true);
        var scratchRoot = kind switch
        {
            // "/." passes the path-character check and only then normalizes to the root.
            "filesystem-root" => Path.GetPathRoot(fakeDocker.Directory) + ".",
            "symbolic-link" => Path.Combine(fakeDocker.Directory, "scratch-link"),
            _ => Path.Combine(fakeDocker.Directory, "missing-scratch")
        };
        if (kind == "symbolic-link")
            Directory.CreateSymbolicLink(scratchRoot, Directory.CreateDirectory(Path.Combine(fakeDocker.Directory, "scratch-target")).FullName);
        var state = new BuildExecutorState();

        await CreateService(fakeDocker, state, scratchRoot).StartAsync(CancellationToken.None);

        Assert.False(state.Snapshot.IsReady);
        Assert.Contains(expectedReason, state.Snapshot.UnavailableReason, StringComparison.OrdinalIgnoreCase);
        // Stale build resources are still reconciled; no image is inspected and no container created.
        Assert.Equal(
            [
                $"container ls --all --quiet --filter label={BuildExecutorDocker.ManagedResourceLabel}",
                "container rm --force stale-build-container",
                $"network ls --quiet --filter label={BuildExecutorDocker.ManagedResourceLabel}",
                $"volume ls --quiet --filter label={BuildExecutorDocker.ManagedResourceLabel}"
            ],
            await fakeDocker.ReadCommands());
        Assert.Equal(kind != "missing", Directory.Exists(scratchRoot));
    }

    [UnixTheory]
    [InlineData(null, null)]
    [InlineData("plugin-builder", null)]
    [InlineData(WorkerImageId, null)]
    [InlineData(null, ProxyImageId)]
    [InlineData(WorkerImageId + "\n", ProxyImageId)]
    [InlineData("btcpayserver/btcpayserver-plugin-builder-worker:latest", ProxyImage)]
    [InlineData("attacker/btcpayserver-plugin-builder-worker:v1.0.76", ProxyImage)]
    [InlineData(ProxyImage, ProxyImage)]
    [InlineData(WorkerImage, WorkerImage)]
    [InlineData(WorkerImage, "btcpayserver/btcpayserver-plugin-builder-proxy:latest")]
    [InlineData(WorkerImage, "attacker/btcpayserver-plugin-builder-proxy:v1.0.76")]
    [InlineData(WorkerImage + "\n", ProxyImage)]
    [InlineData(WorkerImage, ProxyImage + " --privileged")]
    public async Task IncompleteOrUnapprovedImageReferencesFailBeforePullingEitherImage(string? worker, string? proxy)
    {
        await using var docker = await FakeDocker.Create(runscAvailable: true);
        var state = new BuildExecutorState();
        await CreateService(docker, state, workerImage: worker, proxyImage: proxy)
            .StartAsync(CancellationToken.None);
        Assert.False(state.Snapshot.IsReady);
        Assert.False(string.IsNullOrEmpty(state.Snapshot.UnavailableReason));
        Assert.DoesNotContain(await docker.ReadCommands(), c =>
            c.StartsWith("pull ") || c.StartsWith("image inspect ") ||
            c.StartsWith("container create "));
    }

    [UnixFact]
    public async Task ExplicitDevelopmentRuntimeStartsWithoutRunscAndStillRunsAllSmokeChecks()
    {
        await using var docker = await FakeDocker.Create(runscAvailable: false);
        var state = new BuildExecutorState();
        await CreateService(docker, state, useRunc: true).StartAsync(CancellationToken.None);

        Assert.True(state.Snapshot.IsReady);
        var commands = await docker.ReadCommands();
        var smokeContainers = commands.Where(command => command.StartsWith("container create --name plugin-builder-", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, smokeContainers.Length);
        Assert.All(smokeContainers, command => Assert.Contains("--runtime runc", command, StringComparison.Ordinal));
        Assert.DoesNotContain(commands, command => command.StartsWith("info ", StringComparison.Ordinal));
    }

    private static DockerStartupHostedService CreateService(
        FakeDocker fakeDocker,
        BuildExecutorState state,
        string? scratchRoot = null,
        string? workerImage = WorkerImageId,
        string? proxyImage = ProxyImageId,
        bool useRunc = false,
        TimeSpan? dockerOperationTimeout = null)
    {
        var processRunner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
        var options = new BuildExecutorOptions
        {
            BuildScratchRoot = scratchRoot ?? fakeDocker.Directory,
            BuildWorkerImage = workerImage,
            BuildProxyImage = proxyImage,
            UseRunc = useRunc,
            DockerOperationTimeout = dockerOperationTimeout ?? new BuildExecutorOptions().DockerOperationTimeout
        };
        return new DockerStartupHostedService(
            NullLogger<DockerStartupHostedService>.Instance,
            processRunner,
            state,
            new BuildScratchCleaner(
                NullLogger<BuildScratchCleaner>.Instance,
                processRunner, options),
            options);
    }

    private sealed class FakeDocker(FakeDockerHost host) : IAsyncDisposable
    {
        public string Directory => host.Directory;

        public static async Task<FakeDocker> Create(
            bool runscAvailable,
            bool staleContainer = false,
            bool failStaleContainerRemoval = false,
            bool disablePluginBuilds = false,
            string? imageFailure = null,
            bool ambiguousRunscSmokeCreate = false,
            bool stallContainerList = false,
            string? scratchMarkerFailure = null)
        {
            var host = await FakeDockerHost.Start("plugin-builder-startup", $$"""
                #!/bin/sh
                set -eu
                commands="${PB_FAKE_DOCKER_COMMANDS:?}"
                image_failure="${PB_FAKE_IMAGE_FAILURE:-}"
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
                            plugin-builder-runtime-smoke-*)
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
                    pull:--platform)
                        [ "$#" = 4 ] && [ "$3" = linux/amd64 ]
                        case "$4" in
                            {{WorkerImage}}*) role=worker ;;
                            {{ProxyImage}}*) role=proxy ;;
                            *) exit 2 ;;
                        esac
                        : > "${commands}.pull-attempted-${role}"
                        if [ "$image_failure" = "stall-${role}" ]; then
                            : > "${commands}.pull-started"
                            sleep 60
                        fi
                        [ "$image_failure" != "pull-${role}" ] || exit 12
                        ;;
                    image:inspect)
                        [ "$#" = 5 ] && [ "$3" = --format ]
                        target=""
                        for argument in "$@"; do target="$argument"; done
                        case "$target" in
                            {{WorkerImageId}}|{{WorkerImage}}*) role=worker; image_id='{{WorkerImageId}}' ;;
                            {{ProxyImageId}}|{{ProxyImage}}*) role=proxy; image_id='{{ProxyImageId}}' ;;
                            *) exit 2 ;;
                        esac
                        if [ "${target#sha256:}" = "$target" ]; then
                            # A cached image still exists after a failed pull, but no
                            # tag may be inspected before attempting its refresh.
                            [ -f "${commands}.pull-attempted-${role}" ]
                            rm "${commands}.pull-attempted-${role}"
                        fi
                        [ "$image_failure" != "inspect-${role}" ] || exit 12
                        if [ "$image_failure" = "invalid-${role}" ]; then
                            image_id=plugin-builder:latest
                        elif [ "$image_failure" = "mismatch-${role}" ]; then
                            image_id=sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
                        fi
                        : > "${commands}.inspected-${role}"
                        printf '%s\n' "$image_id"
                        ;;
                    info:--format)
                        if [ "${PB_FAKE_RUNSC:-false}" = "true" ]; then
                            printf '%s\n' runsc
                        fi
                        ;;
                    container:create)
                        [ -f "${commands}.inspected-worker" ] && [ -f "${commands}.inspected-proxy" ]
                        previous=""
                        name=""
                        for argument in "$@"; do
                            if [ "$previous" = "--name" ]; then name="$argument"; break; fi
                            previous="$argument"
                        done
                        case "$name" in
                            plugin-builder-scratch-smoke-*)
                                # Capture the production program and its arguments. Only
                                # translate the container mount to this fixture's path.
                                scratch=""
                                while [ "$#" -gt 0 ]; do
                                    case "$1" in
                                        type=bind,source=*,target=/scratch,readonly)
                                            scratch="${1#type=bind,source=}"
                                            scratch="${scratch%,target=/scratch,readonly}"
                                            ;;
                                        -c)
                                            [ -n "$scratch" ]
                                            printf '%s\n' "$2" > "${commands}.probe.${name}"
                                            shift 3
                                            marker="$scratch/${1#/scratch/}"
                                            shift
                                            case "${PB_FAKE_SCRATCH_MARKER_FAILURE:-}" in
                                                missing) marker="${marker}.missing" ;;
                                                mismatched) printf '%s\n' incorrect-marker > "$marker" ;;
                                            esac
                                            printf '%s\n' "$marker" "$@" > "${commands}.probe.${name}.args"
                                            break
                                            ;;
                                    esac
                                    shift
                                done
                                ;;
                            plugin-builder-runtime-smoke-*)
                                if [ "${PB_FAKE_AMBIGUOUS_RUNSC_CREATE:-false}" = "true" ]; then
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
                            plugin-builder-scratch-smoke-*)
                                set --
                                while IFS= read -r argument; do
                                    set -- "$@" "$argument"
                                done < "${commands}.probe.${target}.args"
                                exec /bin/sh -c "$(cat "${commands}.probe.${target}")" scratch-mount-probe "$@"
                                ;;
                        esac
                        ;;
                    *)
                        exit 2
                        ;;
                esac
                """, directory => new()
            {
                ["PBB_DISABLE_PLUGIN_BUILDS"] = disablePluginBuilds ? "true" : null,
                ["PB_FAKE_DOCKER_COMMANDS"] = Path.Combine(directory, "commands"),
                ["PB_FAKE_RUNSC"] = runscAvailable ? "true" : "false",
                ["PB_FAKE_IMAGE_FAILURE"] = imageFailure,
                ["PB_FAKE_STALE_CONTAINER"] = staleContainer ? "true" : "false",
                ["PB_FAKE_REMOVE_FAIL"] = failStaleContainerRemoval ? "true" : "false",
                ["PB_FAKE_AMBIGUOUS_RUNSC_CREATE"] = ambiguousRunscSmokeCreate ? "true" : "false",
                ["PB_FAKE_STALL_CONTAINER_LIST"] = stallContainerList ? "true" : "false",
                ["PB_FAKE_SCRATCH_MARKER_FAILURE"] = scratchMarkerFailure
            });
            return new FakeDocker(host);
        }

        public bool AmbiguousContainerExists =>
            File.Exists(Path.Combine(Directory, "commands.ambiguous-container-exists"));

        public Task WaitForMarker(string name) => host.WaitForFile($"commands.{name}");

        public Task<string[]> ReadCommands() => host.ReadLines("commands");

        public async Task ExposeManagedResources()
        {
            var commands = Path.Combine(Directory, "commands");
            await File.WriteAllTextAsync(commands + ".managed-container", string.Empty);
            await File.WriteAllTextAsync(commands + ".managed-network", string.Empty);
            await File.WriteAllTextAsync(commands + ".managed-volume", string.Empty);
        }

        public ValueTask DisposeAsync() => host.DisposeAsync();
    }

}
