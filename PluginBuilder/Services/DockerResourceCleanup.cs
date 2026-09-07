namespace PluginBuilder.Services;

internal static class DockerResourceCleanup
{
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
                var result = await processRunner.RunAsync(new ProcessSpec
                {
                    Executable = "docker",
                    Arguments = arguments,
                    ErrorCapture = error
                }, timeout.Token);
                if (result == 0)
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
