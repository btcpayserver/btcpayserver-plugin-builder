namespace PluginBuilder.BuildBroker.Configuration;

/// <summary>Trusted broker configuration, never part of a build request.</summary>
public sealed class BuildExecutorOptions
{
    public TimeSpan WorkerExecutionTimeout { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan DockerOperationTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan DockerProbeTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public string? BuildScratchRoot { get; init; }
    public string? BuildWorkerImage { get; init; }
    public bool UseRunc { get; init; }
    public string Runtime => UseRunc ? "runc" : "runsc";
    public string? BuildScratchHostRoot { get; init; }

    public static BuildExecutorOptions FromConfiguration(IConfiguration config, bool isDevelopment)
    {
        var useRunc = config.GetValue<bool>("USE_RUNC");
        var hostRoot = config["BUILD_SCRATCH_HOST_ROOT"];
        if ((useRunc || hostRoot is not null) && !isDevelopment)
            throw new InvalidOperationException("USE_RUNC and BUILD_SCRATCH_HOST_ROOT are development-only settings.");
        if (hostRoot is not null &&
            (!Path.IsPathFullyQualified(hostRoot) || hostRoot.Contains(',') || hostRoot.Any(char.IsControl)))
            throw new InvalidOperationException("BUILD_SCRATCH_HOST_ROOT must be an absolute Docker host path.");
        return new BuildExecutorOptions
        {
            BuildScratchRoot = config["BUILD_SCRATCH_ROOT"],
            BuildScratchHostRoot = hostRoot,
            BuildWorkerImage = config["WORKER_IMAGE"],
            UseRunc = useRunc
        };
    }

    // Docker Desktop stores the development volume inside its Linux VM. Paths
    // used for broker IO and paths sent to the Docker daemon may therefore differ.
    public string DockerPath(string path)
    {
        if (BuildScratchHostRoot is null) return path;
        var relative = Path.GetRelativePath(BuildScratchRoot!, path);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new InvalidOperationException("Docker mount is outside the build scratch directory.");
        return Path.GetFullPath(Path.Combine(BuildScratchHostRoot, relative));
    }
}
