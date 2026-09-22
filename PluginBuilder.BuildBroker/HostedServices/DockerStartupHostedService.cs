using System.Text.RegularExpressions;
using PluginBuilder.BuildBroker.Configuration;
using PluginBuilder.BuildBroker.Services;
using PluginBuilder.Builds;
using PluginBuilder.Builds.Services;

namespace PluginBuilder.BuildBroker.HostedServices;

public class DockerStartupException(string message) : Exception(message);

public class DockerStartupHostedService(
    ILogger<DockerStartupHostedService> logger,
    ProcessRunner processRunner,
    BuildExecutorState executorState,
    BuildScratchCleaner scratchCleaner,
    BuildExecutorOptions options) : IHostedService
{
    private const string DisablePluginBuildsEnvVar = "PBB_DISABLE_PLUGIN_BUILDS";
    private static readonly TimeSpan DockerOperationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ImagePullTimeout = TimeSpan.FromMinutes(5);
    private static readonly Regex ImageIdPattern = new("\\Asha256:[0-9a-f]{64}\\z", RegexOptions.CultureInvariant);
    private static readonly Regex ReleaseTagPattern = new("\\Av[0-9]+\\.[0-9]+\\.[0-9]+([.-][A-Za-z0-9_.-]+)?\\z", RegexOptions.CultureInvariant);
    private static readonly Regex ScratchDirectoryPattern = new("^pb-build-[0-9a-f]{32}$", RegexOptions.CultureInvariant);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        executorState.MarkUnavailable("Build executor startup is in progress");

        try
        {
            // This must run before every early return and preflight. A previous
            // process may have been killed while Docker was still creating or
            // running an untrusted build resource.
            await ReconcileManagedDockerResources(cancellationToken);

            var disablePluginBuildsValue = Environment.GetEnvironmentVariable(DisablePluginBuildsEnvVar);
            var disablePluginBuilds = string.Equals(disablePluginBuildsValue, "1", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(disablePluginBuildsValue, "true", StringComparison.OrdinalIgnoreCase);

            if (disablePluginBuilds)
            {
                logger.LogInformation("Plugin builds are disabled because {DisablePluginBuildsEnvVar}=true", DisablePluginBuildsEnvVar);
                executorState.MarkUnavailable($"{DisablePluginBuildsEnvVar}=true");
                return;
            }

            RequireScratchRoot();

            if (!IsAllowedImageReference(options.BuildWorkerImage, "btcpayserver/btcpayserver-plugin-builder-worker") ||
                !IsAllowedImageReference(options.BuildProxyImage, "btcpayserver/btcpayserver-plugin-builder-proxy"))
                throw new DockerStartupException("WORKER_IMAGE and PROXY_IMAGE must specify an approved release tag or a local sha256 image ID.");
            var workerImageId = await PrepareImage(options.BuildWorkerImage!, cancellationToken);
            var proxyImageId = await PrepareImage(options.BuildProxyImage!, cancellationToken);

            if (options.UseRunc)
                logger.LogWarning("Development builds use runc without gVisor isolation. Run only trusted plugin code.");
            else
                await RequireRunsc(cancellationToken);
            await SmokeTestRuntime(workerImageId, cancellationToken);
            await ReconcileScratchDirectories(workerImageId, cancellationToken);
            await SmokeTestScratchMount(workerImageId, cancellationToken);
            await SmokeTestProxy(proxyImageId, cancellationToken);

            executorState.MarkReady(workerImageId, proxyImageId);
            logger.LogInformation("Build executor is ready");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            executorState.MarkUnavailable("Build executor startup was cancelled");
            throw;
        }
        catch (Exception ex)
        {
            executorState.MarkUnavailable(ex.Message);
            logger.LogCritical(ex,
                "Build executor is unavailable. The public application will remain online, but builds must stay disabled");
        }
    }

    private void RequireScratchRoot()
    {
        var scratchRoot = options.BuildScratchRoot;
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
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        executorState.MarkUnavailable("Build executor shutdown is in progress");

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
                logger.LogCritical(ex,
                    "Could not remove every isolated build resource during application shutdown pass {Pass}",
                    pass + 1);
            }

            if (pass == 0 && !cleanupTimeout.IsCancellationRequested)
                await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
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
        foreach (var directory in Directory.EnumerateDirectories(
                     options.BuildScratchRoot!, "pb-build-*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(directory);
            if (!ScratchDirectoryPattern.IsMatch(name))
                continue;

            logger.LogInformation("Removing stale isolated build scratch directory {ScratchDirectory}", directory);
            if (!await scratchCleaner.TryDeleteAsync(directory, workerImageId, cancellationToken))
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
            logger.LogInformation("Removing stale managed docker {ResourceType} {Resource}", resourceType, resource);
            if (!await DockerResourceCleanup.TryRemoveAsync(
                    processRunner, logger, resourceType, resource, cancellationToken: cancellationToken))
                failures.Add($"Failed to remove managed docker {resourceType} {resource}");
        }

        if (failures.Count > 0)
            throw new DockerStartupException(string.Join("; ", failures));
    }

    private static bool IsAllowedImageReference(string? image, string repository)
    {
        if (image is null) return false;
        if (ImageIdPattern.IsMatch(image)) return true;
        var prefix = repository + ":";
        return image.StartsWith(prefix, StringComparison.Ordinal) &&
               image.Length - prefix.Length <= 128 && ReleaseTagPattern.IsMatch(image[prefix.Length..]);
    }

    private async Task<string> PrepareImage(string image, CancellationToken cancellationToken)
    {
        var isLocalId = ImageIdPattern.IsMatch(image);
        if (!isLocalId)
        {
            // Refresh release tags only at startup, never from a build request.
            // After preflight every build uses the resolved local IDs, not tags.
            logger.LogInformation("Pulling executor image {Image}", image);
            var errors = new OutputCapture();
            var result = await RunDocker(
                ["pull", "--platform", "linux/amd64", image], cancellationToken,
                errorCapture: errors, operationTimeout: ImagePullTimeout);
            if (result != 0)
            {
                logger.LogError("Executor image pull failed for {Image}: {Error}", image, errors);
                throw new DockerStartupException($"Could not pull executor image {image}.");
            }
        }

        var imageId = await InspectImageId(image, cancellationToken);
        // Local IDs keep CI/offline image builds usable without registry access.
        if (isLocalId && imageId != image)
            throw new DockerStartupException("Executor image identity does not match its configured allowlist.");
        return imageId;
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

    private async Task SmokeTestRuntime(string workerImageId, CancellationToken cancellationToken)
    {
        var containerName = $"plugin-builder-runtime-smoke-{Guid.NewGuid():N}";
        var createCompleted = false;
        try
        {
            var createResult = await RunDocker(
                [
                    "container", "create",
                    "--name", containerName,
                    "--label", $"{BuildExecutorDocker.ManagedResourceLabel}=startup-smoke",
                    "--runtime", options.Runtime,
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
                throw new DockerStartupException("Could not create the runtime smoke-test container");
            createCompleted = true;

            var startResult = await RunDocker(
                ["container", "start", "--attach", containerName],
                cancellationToken);
            if (startResult != 0)
                throw new DockerStartupException("The runtime smoke-test container failed");
        }
        finally
        {
            await RemoveSmokeContainer(
                containerName,
                requireQuiescence: !createCompleted,
                "Could not remove the runtime smoke-test container");
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
                    "--runtime", options.Runtime,
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

    private async Task SmokeTestScratchMount(string workerImageId, CancellationToken cancellationToken)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var markerName = $"pb-mount-probe-{suffix}";
        var scratchRoot = options.BuildScratchRoot!;
        var markerPath = Path.Combine(scratchRoot, markerName);
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
                    "--runtime", options.Runtime,
                    "--network", "none",
                    "--read-only",
                    "--user", "0:0",
                    "--cap-drop", "ALL",
                    "--security-opt", "no-new-privileges:true",
                    "--memory", "64m",
                    "--memory-swap", "64m",
                    // The PID budget must also cover gVisor's sandbox and gofer threads.
                    "--pids-limit", "64",
                    "--mount", $"type=bind,source={options.DockerPath(scratchRoot)},target=/scratch,readonly",
                    "--entrypoint", "/bin/sh",
                    workerImageId,
                    "-c",
                    "set -eu; " +
                    "test -f \"$1\"; test \"$(cat -- \"$1\")\" = \"$2\"",
                    "scratch-mount-probe",
                    $"/scratch/{markerName}",
                    suffix
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
                    "The build scratch directory is not shared with the Docker host");
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
                processRunner, logger, "container", containerName, requireQuiescence))
            throw new DockerStartupException(safeError);
    }

    private async Task<int> RunDocker(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        IOutputCapture? outputCapture = null,
        IOutputCapture? errorCapture = null,
        TimeSpan? operationTimeout = null)
    {
        // Pulls need more time than local Docker operations, but neither may
        // hold startup indefinitely. Host cancellation interrupts both.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(operationTimeout ?? DockerOperationTimeout);
        try
        {
            return await processRunner.RunAsync(new ProcessSpec
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
