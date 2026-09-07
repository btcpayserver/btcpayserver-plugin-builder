namespace PluginBuilder.Services;

public sealed class BuildScratchCleaner
{
    private static readonly TimeSpan DockerOperationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ScratchCleanupTimeout = TimeSpan.FromMinutes(5);
    private static readonly string[] ChildDirectoryNames = ["source", "work", "output", "staging"];

    private readonly ILogger<BuildScratchCleaner> _logger;
    private readonly ProcessRunner _processRunner;

    public BuildScratchCleaner(
        ILogger<BuildScratchCleaner> logger,
        ProcessRunner processRunner)
    {
        _logger = logger;
        _processRunner = processRunner;
    }

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
                _logger.LogCritical(
                    "Refusing to mount malformed isolated build scratch directory {ScratchDirectory}",
                    scratchDirectory);
                throw new InvalidOperationException("The isolated build scratch directory is malformed");
            }

            if (childDirectories.Length > 0)
            {
                List<string> createArguments =
                [
                    "container", "create",
                    "--name", cleanupContainer,
                    "--label", $"{BuildExecutorDocker.ManagedResourceLabel}=scratch-cleanup",
                    "--runtime", "runsc",
                    "--network", "none",
                    "--read-only",
                    "--user", "0:0",
                    "--cap-drop", "ALL",
                    "--cap-add", "DAC_OVERRIDE",
                    "--cap-add", "FOWNER",
                    "--security-opt", "no-new-privileges:true",
                    "--memory", "256m",
                    "--memory-swap", "256m",
                    "--cpus", "2",
                    "--pids-limit", "32",
                    "--ulimit", "nofile=128:128",
                    "--stop-timeout", "5",
                    "--log-driver", "none"
                ];

                foreach (var child in childDirectories)
                    createArguments.AddRange(
                        ["--mount", $"type=bind,source={child.Path},target=/{child.Name}"]);

                createArguments.AddRange(
                [
                    "--entrypoint", "/usr/bin/find",
                    workerImageId
                ]);
                createArguments.AddRange(childDirectories.Select(child => $"/{child.Name}"));
                createArguments.AddRange(["-xdev", "-mindepth", "1", "-delete"]);

                createAttempted = true;
                var createCode = await RunDocker(createArguments, DockerOperationTimeout, cancellationToken);
                if (createCode != 0)
                {
                    _logger.LogCritical(
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
                    _logger.LogCritical(
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
            _logger.LogCritical(ex,
                "Failed to remove isolated build scratch directory {ScratchDirectory}",
                scratchDirectory);
        }

        if (!await DockerResourceCleanup.TryRemoveAsync(
                _processRunner, _logger, "container", cleanupContainer,
                ambiguousCreate: createAttempted && !createCompleted))
        {
            _logger.LogCritical(
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

    private async Task<int> RunDocker(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        return await _processRunner.RunAsync(new ProcessSpec
        {
            Executable = "docker",
            Arguments = arguments,
            ErrorCapture = new OutputCapture()
        }, timeoutSource.Token);
    }

}
