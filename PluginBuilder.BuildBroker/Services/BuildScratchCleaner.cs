using PluginBuilder.BuildBroker.Configuration;

namespace PluginBuilder.BuildBroker.Services;

public sealed class BuildScratchCleaner(
    ILogger<BuildScratchCleaner> logger,
    ProcessRunner processRunner,
    BuildExecutorOptions options)
{
    private static readonly TimeSpan ScratchCleanupTimeout = TimeSpan.FromMinutes(5);
    private static readonly string[] ChildDirectoryNames = ["source", "work", "output", "staging"];

    public async Task<bool> TryDeleteAsync(
        string scratchDirectory,
        string workerImageId,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(scratchDirectory))
            return true;

        var cleanupContainer = $"pb-scratch-clean-{Guid.NewGuid():N}";
        var childDirectories = ChildDirectoryNames
            .Select(name => (Name: name, Path: Path.Combine(scratchDirectory, name)))
            .Where(child => Directory.Exists(child.Path))
            .ToArray();
        var scratchDeleted = false;
        var createAttempted = false;
        var createCompleted = false;

        try
        {
            if (IsSymbolicLink(scratchDirectory) ||
                childDirectories.Any(child => IsSymbolicLink(child.Path)) ||
                ChildDirectoryNames
                    .Select(name => Path.Combine(scratchDirectory, name))
                    .Any(path => !Directory.Exists(path) && File.Exists(path)))
            {
                logger.LogCritical(
                    "Refusing to mount malformed isolated build scratch directory {ScratchDirectory}",
                    scratchDirectory);
                throw new InvalidOperationException("The isolated build scratch directory is malformed");
            }

            if (childDirectories.Length > 0)
            {
                // The PID limit also includes gVisor's host-side sandbox threads.
                var createArguments = DockerCli.HardenedContainer(cleanupContainer,
                    $"{BuildExecutorDocker.ManagedResourceLabel}=scratch-cleanup", options.Runtime, "none", "256m", 64,
                    user: "0:0", capAdd: ["DAC_OVERRIDE", "FOWNER"], cpus: "2", nofile: 128, stopTimeout: true,
                    logs: ContainerLogs.None);

                foreach (var child in childDirectories)
                    createArguments.AddRange(
                        ["--mount", $"type=bind,source={options.DockerPath(child.Path)},target=/{child.Name}"]);

                createArguments.AddRange(
                [
                    "--entrypoint", "/usr/bin/find",
                    workerImageId
                ]);
                createArguments.AddRange(childDirectories.Select(child => $"/{child.Name}"));
                createArguments.AddRange(["-xdev", "-mindepth", "1", "-delete"]);

                createAttempted = true;
                var createCode = await RunDocker(createArguments, options.DockerOperationTimeout, cancellationToken);
                if (createCode != 0)
                {
                    logger.LogCritical(
                        "Failed to create the isolated scratch cleanup container for {ScratchDirectory}",
                        scratchDirectory);
                    throw new InvalidOperationException("Could not create the isolated scratch cleanup container");
                }
                createCompleted = true;

                var startCode = await RunDocker(
                    ["container", "start", "--attach", cleanupContainer],
                    ScratchCleanupTimeout,
                    cancellationToken);
                if (startCode != 0)
                {
                    logger.LogCritical(
                        "Failed to empty isolated build scratch directory {ScratchDirectory}",
                        scratchDirectory);
                    throw new InvalidOperationException("Could not empty the isolated build scratch directory");
                }
            }

            foreach (var childDirectory in childDirectories)
                Directory.Delete(childDirectory.Path, recursive: false);
            Directory.Delete(scratchDirectory, recursive: false);
            scratchDeleted = true;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "Failed to remove isolated build scratch directory {ScratchDirectory}",
                scratchDirectory);
        }

        if (!await DockerCli.TryRemoveAsync(
                processRunner, logger, "container", cleanupContainer,
                ambiguousCreate: createAttempted && !createCompleted))
        {
            logger.LogCritical(
                "Failed to remove isolated scratch cleanup container {ContainerName}",
                cleanupContainer);
            return false;
        }

        return scratchDeleted;
    }

    private static bool IsSymbolicLink(string path)
    {
        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
               new DirectoryInfo(path).LinkTarget is not null;
    }

    private Task<int> RunDocker(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken) =>
        DockerCli.RunAsync(processRunner, arguments, timeout, cancellationToken, error: new OutputCapture());
}
