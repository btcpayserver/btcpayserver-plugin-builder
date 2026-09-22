using System.Globalization;
using PluginBuilder.Builds;

namespace PluginBuilder.BuildBroker.Services;

internal enum ContainerLogs { Default, None, Bounded }

internal static class DockerCli
{
    // The isolation baseline of every container the broker creates. Callers only add
    // mounts, tmpfs, environment, entrypoint and image; the optional limits are opt-in
    // so each container keeps exactly the flags it had.
    public static List<string> HardenedContainer(
        string name, string label, string runtime, string network, string memory, int pidsLimit,
        string? user = null, string[]? capAdd = null, string? cpus = null, int? nofile = null,
        bool stopTimeout = false, ContainerLogs logs = ContainerLogs.Default)
    {
        List<string> arguments =
        [
            "container", "create", "--name", name, "--label", label, "--runtime", runtime,
            "--network", network, "--read-only", "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges:true",
            "--memory", memory, "--memory-swap", memory,
            "--pids-limit", pidsLimit.ToString(CultureInfo.InvariantCulture)
        ];
        if (user is not null)
            arguments.AddRange(["--user", user]);
        foreach (var capability in capAdd ?? [])
            arguments.AddRange(["--cap-add", capability]);
        if (cpus is not null)
            arguments.AddRange(["--cpus", cpus]);
        if (nofile is { } files)
            arguments.AddRange(["--ulimit", $"nofile={files}:{files}"]);
        if (stopTimeout)
            arguments.AddRange(["--stop-timeout", "5"]);
        if (logs == ContainerLogs.None)
            arguments.AddRange(["--log-driver", "none"]);
        else if (logs == ContainerLogs.Bounded)
            arguments.AddRange(["--log-opt", "max-size=1m", "--log-opt", "max-file=1"]);
        return arguments;
    }

    // Every docker invocation is bounded. The timeout and the caller's token both surface
    // as OperationCanceledException; callers tell them apart by checking their own token.
    public static async Task<int> RunAsync(
        ProcessRunner processRunner,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IOutputCapture? output = null,
        IOutputCapture? error = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        return await processRunner.RunAsync(new ProcessSpec
        {
            Executable = "docker",
            Arguments = arguments,
            OutputCapture = output,
            ErrorCapture = error
        }, deadline.Token);
    }

    public static async Task<bool> TryRemoveAsync(
        ProcessRunner processRunner,
        ILogger logger,
        string kind,
        string name,
        bool ambiguousCreate = false,
        CancellationToken cancellationToken = default)
    {
        string[] arguments = kind switch
        {
            "container" or "volume" => [kind, "rm", "--force", name],
            "network" => [kind, "rm", name],
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            while (true)
            {
                var error = new OutputCapture();
                if (await RunAsync(processRunner, arguments, TimeSpan.FromSeconds(30), timeout.Token, error: error) == 0)
                    return true;

                var details = error.ToString();
                var notFound = details.Contains($"No such {kind}", StringComparison.OrdinalIgnoreCase) ||
                               details.Contains($"{kind} {name} not found", StringComparison.OrdinalIgnoreCase);
                if (notFound && !ambiguousCreate)
                    return true;
                if (!notFound)
                {
                    logger.LogCritical("Failed to remove managed docker {Kind} {Name}: {Error}", kind, name, details.Trim());
                    return false;
                }

                // A timed-out create may still reach the daemon after the first rm.
                // Only a successful removal proves quiescence in that case.
                await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
            }
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Could not confirm removal of managed docker {Kind} {Name}", kind, name);
            return false;
        }
    }
}
