namespace PluginBuilder.HostedServices;

public class DockerStartupException : Exception
{
    public DockerStartupException(string message) : base(message)
    {
    }

    public static bool DockerStartupCompleted { get; private set; }
    public static Exception? DockerStartupError { get; private set; }

    public static void ResetStartupState()
    {
        DockerStartupCompleted = false;
        DockerStartupError = null;
    }

    public static void MarkStartupCompleted()
    {
        DockerStartupCompleted = true;
    }

    public static void MarkStartupFailed(Exception ex)
    {
        DockerStartupError = ex;
        DockerStartupCompleted = false;
    }
}

public class DockerStartupHostedService : IHostedService
{
    private const string SkipBuildEnvVar = "DOCKER_STARTUP_SKIP_BUILD";

    public DockerStartupHostedService(ILogger<DockerStartupHostedService> logger, IWebHostEnvironment env, ProcessRunner processRunner)
    {
        Logger = logger;
        ProcessRunner = processRunner;
        ContentRootPath = env.ContentRootPath;
    }

    public ILogger<DockerStartupHostedService> Logger { get; }
    public ProcessRunner ProcessRunner { get; }
    public string ContentRootPath { get; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        DockerStartupException.ResetStartupState();

        var skipBuildValue = Environment.GetEnvironmentVariable(SkipBuildEnvVar);
        var skipBuild = string.Equals(skipBuildValue, "1", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(skipBuildValue, "true", StringComparison.OrdinalIgnoreCase);

        if (skipBuild)
        {
            Logger.LogInformation("Skipping docker image build because {SkipBuildEnvVar}=true", SkipBuildEnvVar);
            DockerStartupException.MarkStartupCompleted();
            return;
        }

        try
        {
            Logger.LogInformation("Building the PluginBuilder docker image");

            var buildResult = await ProcessRunner.RunAsync(new ProcessSpec
            {
                Executable = "docker",
                EnvironmentVariables =
                {
                    // Somehow we get permission problem when buildkit isn't used
                    ["DOCKER_BUILDKIT"] = "1"
                },
                Arguments = new[] { "build", "-f", "PluginBuilder.Dockerfile", "-t", "plugin-builder", "." },
                WorkingDirectory = ContentRootPath
            }, cancellationToken);
            if (buildResult != 0)
                throw new DockerStartupException("The build of PluginBuilder.Dockerfile failed");

            await CleanupDanglingBuildVolumes(cancellationToken);
            DockerStartupException.MarkStartupCompleted();
        }
        catch (Exception ex)
        {
            DockerStartupException.MarkStartupFailed(ex);
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private async Task CleanupDanglingBuildVolumes(CancellationToken cancellationToken)
    {
        OutputCapture output = new();
        var result = await ProcessRunner.RunAsync(
            new ProcessSpec
            {
                Executable = "docker",
                Arguments = ["volume", "ls", "-f", "label=BTCPAY_PLUGIN_BUILD", "--format", "{{ .Name }}"],
                OutputCapture = output
            }, cancellationToken);
        if (result != 0)
            throw new DockerStartupException("docker volume ls failed");
        if (!output.Lines.Any())
            return;

        Logger.LogInformation("Cleaning dangling build volumes");
        foreach (var volume in output.Lines)
        {
            result = await ProcessRunner.RunAsync(new ProcessSpec
            {
                Executable = "docker",
                Arguments = ["volume", "rm", volume]
            }, cancellationToken);
            if (result != 0)
                Logger.LogWarning("Failed to remove dangling docker volume {Volume}", volume);
        }
    }
}
