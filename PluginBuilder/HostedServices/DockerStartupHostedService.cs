using System.Text.RegularExpressions;
using PluginBuilder.Configuration;
using PluginBuilder.Services;

namespace PluginBuilder.HostedServices;

public class DockerStartupException : Exception
{
    public DockerStartupException(string message) : base(message)
    {
    }
}

public class DockerStartupHostedService : IHostedService
{
    private const string SkipBuildEnvVar = "DOCKER_STARTUP_SKIP_BUILD";
    private const string WorkerDockerfile = "PluginBuilder.Dockerfile";
    private const string ProxyDockerfile = "PluginBuilder.Proxy.Dockerfile";
    private const long MaxScratchFilesystemBytes = 16L * 1024 * 1024 * 1024;
    private const long MinScratchAvailableBytes = 4L * 1024 * 1024 * 1024;
    private const long MaxScratchFilesystemInodes = 65_536;
    private const long MinScratchAvailableInodes = 8_192;
    private static readonly TimeSpan DockerOperationTimeout = TimeSpan.FromSeconds(30);
    private static readonly Regex ImageIdPattern = new("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex ScratchDirectoryPattern = new("^pb-build-[0-9a-f]{32}$", RegexOptions.CultureInvariant);

    public DockerStartupHostedService(
        ILogger<DockerStartupHostedService> logger,
        IWebHostEnvironment env,
        ProcessRunner processRunner,
        BuildExecutorState executorState,
        BuildScratchCleaner scratchCleaner,
        PluginBuilderOptions options)
    {
        Logger = logger;
        ProcessRunner = processRunner;
        ExecutorState = executorState;
        ScratchCleaner = scratchCleaner;
        Options = options;
        ContentRootPath = env.ContentRootPath;
    }

    public ILogger<DockerStartupHostedService> Logger { get; }
    public ProcessRunner ProcessRunner { get; }
    public BuildExecutorState ExecutorState { get; }
    public BuildScratchCleaner ScratchCleaner { get; }
    public PluginBuilderOptions Options { get; }
    public string ContentRootPath { get; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ExecutorState.MarkUnavailable("Build executor startup is in progress");

        try
        {
            // This must run before every early return and preflight. A previous
            // process may have been killed while Docker was still creating or
            // running an untrusted build resource.
            await ReconcileManagedDockerResources(cancellationToken);

            var skipBuildValue = Environment.GetEnvironmentVariable(SkipBuildEnvVar);
            var skipBuild = string.Equals(skipBuildValue, "1", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(skipBuildValue, "true", StringComparison.OrdinalIgnoreCase);

            if (skipBuild)
            {
                Logger.LogInformation("Skipping docker image build because {SkipBuildEnvVar}=true", SkipBuildEnvVar);
                ExecutorState.MarkUnavailable($"{SkipBuildEnvVar}=true");
                return;
            }

            RequireScratchRoot();
            await RequireDedicatedScratchFilesystem(cancellationToken);
            await SmokeTestOutputLimiter(cancellationToken);

            await BuildImage(WorkerDockerfile, BuildExecutorDocker.WorkerImageTag, cancellationToken);
            await BuildImage(ProxyDockerfile, BuildExecutorDocker.ProxyImageTag, cancellationToken);

            var workerImageId = await InspectImageId(BuildExecutorDocker.WorkerImageTag, cancellationToken);
            var proxyImageId = await InspectImageId(BuildExecutorDocker.ProxyImageTag, cancellationToken);

            await RequireRunsc(cancellationToken);
            await SmokeTestRunsc(workerImageId, cancellationToken);
            await ReconcileScratchDirectories(workerImageId, cancellationToken);
            foreach (var slot in ScratchSlots())
                await SmokeTestScratchMount(slot, workerImageId, cancellationToken);
            await SmokeTestProxy(proxyImageId, cancellationToken);

            ExecutorState.MarkReady(workerImageId, proxyImageId);
            Logger.LogInformation("Build executor is ready");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ExecutorState.MarkUnavailable("Build executor startup was cancelled");
            throw;
        }
        catch (Exception ex)
        {
            ExecutorState.MarkUnavailable(ex.Message);
            Logger.LogCritical(ex,
                "Build executor is unavailable. The public application will remain online, but builds must stay disabled");
        }
    }

    private void RequireScratchRoot()
    {
        var scratchRoot = Options.BuildScratchRoot;
        if (scratchRoot is null)
            throw new DockerStartupException("BUILD_SCRATCH_ROOT is not configured");
        if (!Path.IsPathFullyQualified(scratchRoot))
            throw new DockerStartupException("BUILD_SCRATCH_ROOT must be an absolute path");
        if (!Directory.Exists(scratchRoot))
            throw new DockerStartupException("BUILD_SCRATCH_ROOT does not exist");
        if (!Regex.IsMatch(scratchRoot, "^/[A-Za-z0-9._/-]+$", RegexOptions.CultureInvariant))
            throw new DockerStartupException("BUILD_SCRATCH_ROOT contains unsupported path characters");

        var fullPath = Path.GetFullPath(scratchRoot);
        var rootPath = Path.GetPathRoot(fullPath);
        if (string.Equals(
                fullPath.TrimEnd(Path.DirectorySeparatorChar),
                rootPath?.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.Ordinal))
            throw new DockerStartupException("BUILD_SCRATCH_ROOT cannot be a filesystem root");

        var attributes = File.GetAttributes(fullPath);
        if ((attributes & FileAttributes.ReparsePoint) != 0 ||
            new DirectoryInfo(fullPath).LinkTarget is not null)
            throw new DockerStartupException("BUILD_SCRATCH_ROOT cannot be a symbolic link");

        foreach (var slot in ScratchSlots())
        {
            if (!Directory.Exists(slot) || (File.GetAttributes(slot) & FileAttributes.ReparsePoint) != 0)
                throw new DockerStartupException("Each build scratch slot must exist and cannot be a symbolic link");
        }
    }

    private IEnumerable<string> ScratchSlots() => Enumerable.Range(0, DockerBuildSandbox.MaxConcurrentBuilds)
        .Select(slot => DockerBuildSandbox.ScratchSlotPath(Options.BuildScratchRoot!, slot));

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        ExecutorState.MarkUnavailable("Build executor shutdown is in progress");

        using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        for (var pass = 0; pass < 2; pass++)
        {
            try
            {
                // Run twice to close the narrow race with a build that was between
                // its readiness check and docker create when shutdown began.
                await ReconcileManagedDockerResources(cleanupTimeout.Token);
            }
            catch (Exception ex)
            {
                Logger.LogCritical(ex,
                    "Could not remove every isolated build resource during application shutdown pass {Pass}",
                    pass + 1);
            }

            if (pass == 0 && !cleanupTimeout.IsCancellationRequested)
                await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }

    protected virtual async Task RequireDedicatedScratchFilesystem(CancellationToken cancellationToken)
    {
        foreach (var slot in ScratchSlots())
        {
            var mountPointResult = await ProcessRunner.RunAsync(new ProcessSpec
            {
                Executable = "/usr/bin/mountpoint",
                Arguments = ["--quiet", "--", slot],
                ErrorCapture = new OutputCapture()
            }, cancellationToken);
            if (mountPointResult != 0)
                throw new DockerStartupException("Each build scratch slot must be a dedicated mount point");
        }

        var output = new OutputCapture();
        var result = await ProcessRunner.RunAsync(new ProcessSpec
        {
            Executable = "/usr/bin/stat",
            Arguments = ["--format=%d", "--", .. ScratchSlots(), Options.DataDir],
            OutputCapture = output,
            ErrorCapture = new OutputCapture()
        }, cancellationToken);

        var deviceIds = output.Lines
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
        if (result != 0 || deviceIds.Length != DockerBuildSandbox.MaxConcurrentBuilds + 1 ||
            deviceIds.Any(id => !ulong.TryParse(id, out _)))
            throw new DockerStartupException("Could not verify the build scratch filesystem");
        if (deviceIds.Distinct().Count() != deviceIds.Length)
            throw new DockerStartupException(
                "Build scratch slots must use different filesystems, separate from application data");
    }

    private async Task SmokeTestOutputLimiter(CancellationToken cancellationToken)
    {
        var limiterPath = BuildExecutorDocker.OutputLimiterPath;
        if (!File.Exists(limiterPath) ||
            (File.GetAttributes(limiterPath) & FileAttributes.ReparsePoint) != 0)
            throw new DockerStartupException("The trusted build output limiter is unavailable");

        var result = await ProcessRunner.RunAsync(new ProcessSpec
        {
            Executable = "/usr/bin/perl",
            Arguments =
            [
                limiterPath,
                "1024", "256", "4", "5",
                "--", "/usr/bin/true"
            ],
            OutputCapture = new OutputCapture(),
            ErrorCapture = new OutputCapture()
        }, cancellationToken);

        if (result != 0)
            throw new DockerStartupException("The trusted build output limiter failed its smoke test");
    }

    private async Task ReconcileManagedDockerResources(CancellationToken cancellationToken)
    {
        List<string> failures = [];

        async Task Reconcile(
            string resourceType,
            IReadOnlyList<string> listArguments)
        {
            try
            {
                await RemoveResources(resourceType, listArguments, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add(ex.Message);
            }
        }

        await Reconcile(
            "container",
            ["container", "ls", "--all", "--quiet", "--filter", $"label={BuildExecutorDocker.ManagedResourceLabel}"]);

        await Reconcile(
            "network",
            ["network", "ls", "--quiet", "--filter", $"label={BuildExecutorDocker.ManagedResourceLabel}"]);

        await Reconcile(
            "volume",
            ["volume", "ls", "--quiet", "--filter", $"label={BuildExecutorDocker.ManagedResourceLabel}"]);

        if (failures.Count > 0)
            throw new DockerStartupException(string.Join("; ", failures));
    }

    private async Task ReconcileScratchDirectories(
        string workerImageId,
        CancellationToken cancellationToken)
    {
        foreach (var directory in ScratchSlots().SelectMany(slot =>
                     Directory.EnumerateDirectories(slot, "pb-build-*", SearchOption.TopDirectoryOnly)))
        {
            var name = Path.GetFileName(directory);
            if (!ScratchDirectoryPattern.IsMatch(name))
                continue;

            Logger.LogInformation("Removing stale isolated build scratch directory {ScratchDirectory}", directory);
            if (!await ScratchCleaner.TryDeleteAsync(directory, workerImageId, cancellationToken))
                throw new DockerStartupException($"Failed to remove managed build scratch directory {name}");
        }
    }

    private async Task RemoveResources(
        string resourceType,
        IReadOnlyList<string> listArguments,
        CancellationToken cancellationToken)
    {
        var resources = new OutputCapture();
        var result = await RunDocker(listArguments, cancellationToken, resources);
        if (result != 0)
            throw new DockerStartupException($"docker {resourceType} reconciliation failed while listing resources");

        List<string> failures = [];
        foreach (var resource in resources.Lines.Where(resource => !string.IsNullOrWhiteSpace(resource)))
        {
            Logger.LogInformation("Removing stale managed docker {ResourceType} {Resource}", resourceType, resource);
            if (!await DockerResourceCleanup.TryRemoveAsync(
                    ProcessRunner, Logger, resourceType, resource, cancellationToken: cancellationToken))
                failures.Add($"Failed to remove managed docker {resourceType} {resource}");
        }

        if (failures.Count > 0)
            throw new DockerStartupException(string.Join("; ", failures));
    }

    private async Task BuildImage(string dockerfile, string tag, CancellationToken cancellationToken)
    {
        Logger.LogInformation("Building docker image {ImageTag} from {Dockerfile}", tag, dockerfile);
        var result = await ProcessRunner.RunAsync(new ProcessSpec
        {
            Executable = "docker",
            EnvironmentVariables =
            {
                // Somehow we get permission problems when BuildKit isn't used.
                ["DOCKER_BUILDKIT"] = "1"
            },
            Arguments = ["build", "--platform", "linux/amd64", "-f", dockerfile, "-t", tag, "."],
            WorkingDirectory = ContentRootPath
        }, cancellationToken);

        if (result != 0)
            throw new DockerStartupException($"The build of {dockerfile} failed");
    }

    private async Task<string> InspectImageId(string image, CancellationToken cancellationToken)
    {
        var output = new OutputCapture();
        var result = await RunDocker(
            ["image", "inspect", "--format", "{{.Id}}", image],
            cancellationToken,
            output);
        var imageId = output.Lines.SingleOrDefault()?.Trim();

        if (result != 0 || imageId is null || !ImageIdPattern.IsMatch(imageId))
            throw new DockerStartupException($"Could not resolve an immutable image ID for {image}");

        return imageId;
    }

    private async Task RequireRunsc(CancellationToken cancellationToken)
    {
        var output = new OutputCapture();
        var result = await RunDocker(
            ["info", "--format", "{{if index .Runtimes \"runsc\"}}runsc{{end}}"],
            cancellationToken,
            output);

        if (result != 0 || !output.Lines.Any(line => string.Equals(line.Trim(), "runsc", StringComparison.Ordinal)))
            throw new DockerStartupException("The runsc Docker runtime is not configured");
    }

    private async Task SmokeTestRunsc(string workerImageId, CancellationToken cancellationToken)
    {
        var containerName = $"plugin-builder-runsc-smoke-{Guid.NewGuid():N}";
        var createCompleted = false;
        try
        {
            var createResult = await RunDocker(
                [
                    "container", "create",
                    "--name", containerName,
                    "--label", $"{BuildExecutorDocker.ManagedResourceLabel}=startup-smoke",
                    "--runtime", "runsc",
                    "--network", "none",
                    "--read-only",
                    "--cap-drop", "ALL",
                    "--security-opt", "no-new-privileges:true",
                    "--memory", "128m",
                    "--memory-swap", "128m",
                    "--pids-limit", "64",
                    "--entrypoint", "/bin/true",
                    workerImageId
                ],
                cancellationToken);
            if (createResult != 0)
                throw new DockerStartupException("Could not create the runsc smoke-test container");
            createCompleted = true;

            var startResult = await RunDocker(
                ["container", "start", "--attach", containerName],
                cancellationToken);
            if (startResult != 0)
                throw new DockerStartupException("The runsc smoke-test container failed");
        }
        finally
        {
            await RemoveSmokeContainer(
                containerName,
                requireQuiescence: !createCompleted,
                "Could not remove the runsc smoke-test container");
        }
    }

    private async Task SmokeTestProxy(string proxyImageId, CancellationToken cancellationToken)
    {
        var containerName = $"plugin-builder-proxy-smoke-{Guid.NewGuid():N}";
        var createCompleted = false;
        try
        {
            var createResult = await RunDocker(
                [
                    "container", "create",
                    "--name", containerName,
                    "--label", $"{BuildExecutorDocker.ManagedResourceLabel}=startup-smoke",
                    "--runtime", "runsc",
                    "--network", "none",
                    "--read-only",
                    "--user", "13:13",
                    "--cap-drop", "ALL",
                    "--security-opt", "no-new-privileges:true",
                    "--memory", "128m",
                    "--memory-swap", "128m",
                    "--pids-limit", "64",
                    "--tmpfs", "/tmp:rw,noexec,nosuid,nodev,size=16m,mode=0700,uid=13,gid=13",
                    "--tmpfs", "/run/squid:rw,noexec,nosuid,nodev,size=4m,mode=0700,uid=13,gid=13",
                    "--tmpfs", "/var/log/squid:rw,noexec,nosuid,nodev,size=16m,mode=0700,uid=13,gid=13",
                    "--tmpfs", "/var/spool/squid:rw,noexec,nosuid,nodev,size=16m,mode=0700,uid=13,gid=13",
                    proxyImageId,
                    "-k", "parse", "-f", "/etc/squid/squid.conf"
                ],
                cancellationToken);
            if (createResult != 0)
                throw new DockerStartupException("Could not create the build proxy smoke-test container");
            createCompleted = true;

            var startResult = await RunDocker(["container", "start", "--attach", containerName], cancellationToken);
            if (startResult != 0)
                throw new DockerStartupException("The build proxy configuration smoke test failed");
        }
        finally
        {
            await RemoveSmokeContainer(
                containerName,
                requireQuiescence: !createCompleted,
                "Could not remove the build proxy smoke-test container");
        }
    }

    private async Task SmokeTestScratchMount(string scratchSlot, string workerImageId, CancellationToken cancellationToken)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var markerName = $"pb-mount-probe-{suffix}";
        var markerPath = Path.Combine(scratchSlot, markerName);
        var containerName = $"plugin-builder-scratch-smoke-{suffix}";
        await File.WriteAllTextAsync(markerPath, suffix, cancellationToken);
        var createCompleted = false;

        try
        {
            var createResult = await RunDocker(
                [
                    "container", "create",
                    "--name", containerName,
                    "--label", $"{BuildExecutorDocker.ManagedResourceLabel}=startup-smoke",
                    "--runtime", "runsc",
                    "--network", "none",
                    "--read-only",
                    "--user", "0:0",
                    "--cap-drop", "ALL",
                    "--security-opt", "no-new-privileges:true",
                    "--memory", "64m",
                    "--memory-swap", "64m",
                    "--pids-limit", "16",
                    "--mount", $"type=bind,source={scratchSlot},target=/scratch,readonly",
                    "--entrypoint", "/bin/sh",
                    workerImageId,
                    "-c",
                    "set -eu; " +
                    "test -f \"$1\"; " +
                    "block_size=$(stat -f -c %S -- \"$2\"); " +
                    "total_blocks=$(stat -f -c %b -- \"$2\"); " +
                    "available_blocks=$(stat -f -c %a -- \"$2\"); " +
                    "total_inodes=$(stat -f -c %c -- \"$2\"); " +
                    "available_inodes=$(stat -f -c %d -- \"$2\"); " +
                    "test $((block_size * total_blocks)) -le \"$3\"; " +
                    "test $((block_size * available_blocks)) -ge \"$4\"; " +
                    "test \"$total_inodes\" -le \"$5\"; " +
                    "test \"$available_inodes\" -ge \"$6\"",
                    "scratch-mount-probe",
                    $"/scratch/{markerName}",
                    "/scratch",
                    MaxScratchFilesystemBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    MinScratchAvailableBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    MaxScratchFilesystemInodes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    MinScratchAvailableInodes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ],
                cancellationToken);

            if (createResult != 0)
                throw new DockerStartupException("The build scratch directory is not shared with the Docker host");
            createCompleted = true;

            var startResult = await RunDocker(
                ["container", "start", "--attach", containerName],
                cancellationToken);
            if (startResult != 0)
                throw new DockerStartupException(
                    "The build scratch directory is not shared with the Docker host or is not a bounded dedicated filesystem");
        }
        finally
        {
            DockerStartupException? removalFailure = null;
            try
            {
                await RemoveSmokeContainer(
                    containerName,
                    requireQuiescence: !createCompleted,
                    "Could not remove the build scratch smoke-test container");
            }
            catch (DockerStartupException ex)
            {
                removalFailure = ex;
            }

            try
            {
                File.Delete(markerPath);
            }
            catch (Exception ex)
            {
                var cleanupContext = removalFailure is null
                    ? string.Empty
                    : $"{removalFailure.Message}; additionally, ";
                throw new DockerStartupException(
                    $"{cleanupContext}could not remove the build scratch probe file: {ex.Message}");
            }

            if (removalFailure is not null)
                throw removalFailure;
        }
    }

    private async Task RemoveSmokeContainer(
        string containerName,
        bool requireQuiescence,
        string safeError)
    {
        if (!await DockerResourceCleanup.TryRemoveAsync(
                ProcessRunner, Logger, "container", containerName, requireQuiescence))
            throw new DockerStartupException(safeError);
    }

    private async Task<int> RunDocker(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        IOutputCapture? outputCapture = null,
        IOutputCapture? errorCapture = null)
    {
        // Short Docker operations must not keep the public application waiting
        // indefinitely for an optional executor. Image builds run separately.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DockerOperationTimeout);
        try
        {
            return await ProcessRunner.RunAsync(new ProcessSpec
            {
                Executable = "docker",
                Arguments = arguments,
                OutputCapture = outputCapture,
                ErrorCapture = errorCapture ?? new OutputCapture()
            }, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new DockerStartupException("Docker startup operation timed out.");
        }
    }
}
