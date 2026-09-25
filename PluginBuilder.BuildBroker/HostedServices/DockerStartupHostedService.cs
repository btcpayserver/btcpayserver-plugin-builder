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
    private static readonly TimeSpan ImagePullTimeout = TimeSpan.FromMinutes(5);
    private static readonly Regex ImageIdPattern = new("\\Asha256:[0-9a-f]{64}\\z", RegexOptions.CultureInvariant);
    private static readonly Regex ReleaseTagPattern = new("\\Av[0-9]+\\.[0-9]+\\.[0-9]+([.-][A-Za-z0-9_.-]+)?\\z", RegexOptions.CultureInvariant);
    private static readonly Regex ScratchDirectoryPattern = new("^pb-build-[0-9a-f]{32}$", RegexOptions.CultureInvariant);
    private static readonly Regex ProbeFilePattern = new("^pb-(mount-probe|proxy-config)-[0-9a-f]{32}$", RegexOptions.CultureInvariant);
    private const string SmokeLabel = BuildExecutorDocker.ManagedResourceLabel + "=startup-smoke";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        executorState.MarkUnavailable("Build executor startup is in progress");

        try
        {
            // This must run before every early return and preflight. A previous
            // process may have been killed while Docker was still creating or
            // running an untrusted build resource.
            await ReconcileManagedDockerResources(cancellationToken);

            if (options.DisablePluginBuilds)
            {
                logger.LogInformation("Plugin builds are disabled because PBB_DISABLE_PLUGIN_BUILDS=true");
                executorState.MarkUnavailable("PBB_DISABLE_PLUGIN_BUILDS=true");
                return;
            }

            RequireScratchRoot();

            if (!IsAllowedImageReference(options.BuildWorkerImage, "btcpayserver/btcpayserver-plugin-builder-worker"))
                throw new DockerStartupException("WORKER_IMAGE must specify an approved release tag or a local sha256 image ID.");
            var workerImageId = await PrepareImage(options.BuildWorkerImage!, cancellationToken);
            var proxyImageId = await PrepareImage(DockerBuildSandbox.ProxyImage, cancellationToken);

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

        // Upgrades from the pre-isolation builder may leave labeled build volumes behind.
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
        RemoveStaleProbeFiles();
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

    // Probe files are written only by the broker, directly in the scratch root and
    // outside every sandbox mount, so they can be removed without a cleanup container.
    private void RemoveStaleProbeFiles()
    {
        foreach (var file in Directory.EnumerateFiles(options.BuildScratchRoot!, "pb-*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            if (!ProbeFilePattern.IsMatch(name))
                continue;

            logger.LogInformation("Removing stale startup probe file {ProbeFile}", file);
            try
            {
                File.Delete(file);
            }
            catch (Exception ex)
            {
                throw new DockerStartupException($"Failed to remove stale startup probe file {name}: {ex.Message}");
            }
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
            if (!await DockerCli.TryRemoveAsync(
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
        // A digest reference can only resolve to that content, so a present copy needs no registry.
        if (image.Contains("@sha256:", StringComparison.Ordinal) &&
            await TryInspectImageId(image, cancellationToken) is { } pinnedImageId)
            return pinnedImageId;

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

    private async Task<string> InspectImageId(string image, CancellationToken cancellationToken) =>
        await TryInspectImageId(image, cancellationToken)
        ?? throw new DockerStartupException($"Could not resolve an immutable image ID for {image}");

    private async Task<string?> TryInspectImageId(string image, CancellationToken cancellationToken)
    {
        var output = new OutputCapture();
        var result = await RunDocker(
            ["image", "inspect", "--format", "{{.Id}}", image],
            cancellationToken,
            output);
        var imageId = output.Lines.SingleOrDefault()?.Trim();
        return result == 0 && imageId is not null && ImageIdPattern.IsMatch(imageId) ? imageId : null;
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

    private Task SmokeTestRuntime(string workerImageId, CancellationToken cancellationToken)
    {
        var name = $"plugin-builder-runtime-smoke-{Guid.NewGuid():N}";
        var arguments = DockerCli.HardenedContainer(name, SmokeLabel, options.Runtime, "none", "128m", 64);
        arguments.AddRange(["--entrypoint", "/bin/true", workerImageId]);
        return RunSmokeContainer(name, arguments, "Could not create the runtime smoke-test container",
            "The runtime smoke-test container failed", "Could not remove the runtime smoke-test container", cancellationToken);
    }

    private async Task SmokeTestProxy(string proxyImageId, CancellationToken cancellationToken)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var configurationPath = Path.Combine(options.BuildScratchRoot!, $"pb-proxy-config-{suffix}");
        var name = $"plugin-builder-proxy-smoke-{suffix}";
        var arguments = DockerCli.HardenedContainer(name, SmokeLabel, options.Runtime, "none", "128m", 64,
            user: DockerBuildSandbox.ProxyUser);
        arguments.AddRange(DockerBuildSandbox.ProxyTmpfsArguments);
        arguments.AddRange(DockerBuildSandbox.ProxyProgramArguments(options.DockerPath(configurationPath)));
        arguments.AddRange([proxyImageId, "-k", "parse", "-f", DockerBuildSandbox.ProxyConfigurationPath]);
        await WithProbeFile(
            configurationPath,
            () => DockerBuildSandbox.WriteReadOnlyFile(configurationPath, DockerBuildSandbox.ProxyConfiguration),
            "Could not remove the build proxy configuration probe file",
            () => RunSmokeContainer(name, arguments, "Could not create the build proxy smoke-test container",
                "The build proxy configuration smoke test failed", "Could not remove the build proxy smoke-test container",
                cancellationToken));
    }

    private async Task SmokeTestScratchMount(string workerImageId, CancellationToken cancellationToken)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var markerName = $"pb-mount-probe-{suffix}";
        var scratchRoot = options.BuildScratchRoot!;
        var markerPath = Path.Combine(scratchRoot, markerName);
        var name = $"plugin-builder-scratch-smoke-{suffix}";
        // The PID budget must also cover gVisor's sandbox and gofer threads.
        var arguments = DockerCli.HardenedContainer(name, SmokeLabel, options.Runtime, "none", "64m", 64, user: "0:0");
        arguments.AddRange(
        [
            "--mount", $"type=bind,source={options.DockerPath(scratchRoot)},target=/scratch,readonly",
            "--entrypoint", "/bin/sh",
            workerImageId,
            "-c",
            "set -eu; " +
            "test -f \"$1\"; test \"$(cat -- \"$1\")\" = \"$2\"",
            "scratch-mount-probe",
            $"/scratch/{markerName}",
            suffix
        ]);
        const string notShared = "The build scratch directory is not shared with the Docker host";
        await WithProbeFile(
            markerPath,
            () => File.WriteAllText(markerPath, suffix),
            "Could not remove the build scratch probe file",
            () => RunSmokeContainer(name, arguments, notShared, notShared,
                "Could not remove the build scratch smoke-test container", cancellationToken));
    }

    // A failed probe keeps its own error; only a successful probe fails on cleanup.
    private async Task WithProbeFile(string path, Action write, string removeError, Func<Task> probe)
    {
        try
        {
            write();
            await probe();
        }
        catch
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not remove startup probe file {ProbeFile}", path);
            }
            throw;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            throw new DockerStartupException($"{removeError}: {ex.Message}");
        }
    }

    private async Task RunSmokeContainer(string name, IReadOnlyList<string> createArguments,
        string createError, string runError, string removeError, CancellationToken cancellationToken)
    {
        var createCompleted = false;
        try
        {
            if (await RunDocker(createArguments, cancellationToken) != 0)
                throw new DockerStartupException(createError);
            createCompleted = true;
            if (await RunDocker(["container", "start", "--attach", name], cancellationToken) != 0)
                throw new DockerStartupException(runError);
        }
        finally
        {
            // An interrupted create may still reach the daemon: only a removal proves quiescence.
            if (!await DockerCli.TryRemoveAsync(processRunner, logger, "container", name, ambiguousCreate: !createCompleted))
                throw new DockerStartupException(removeError);
        }
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
        try
        {
            return await DockerCli.RunAsync(processRunner, arguments, operationTimeout ?? options.DockerOperationTimeout,
                cancellationToken, outputCapture, errorCapture);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DockerStartupException("Docker startup operation timed out.");
        }
    }
}
