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
        var skipBuildValue = Environment.GetEnvironmentVariable(SkipBuildEnvVar);
        var skipBuild = string.Equals(skipBuildValue, "1", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(skipBuildValue, "true", StringComparison.OrdinalIgnoreCase);

        if (skipBuild)
        {
            Logger.LogInformation("Skipping docker image build because {SkipBuildEnvVar}=true", SkipBuildEnvVar);
        }
        else
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
        }

        OutputCapture output = new();
        var result = await ProcessRunner.RunAsync(
            new ProcessSpec
            {
                Executable = "docker",
                Arguments = new[] { "volume", "ls", "-f", "label=BTCPAY_PLUGIN_BUILD", "--format", "{{ .Name }}" },
                OutputCapture = output
            }, cancellationToken);
        if (result != 0)
            throw new DockerStartupException("docker volume ls failed");
        if (output.Lines.Any())
        {
            Logger.LogInformation("Cleaning dangling volumes");
            foreach (var volume in output.Lines)
            {
                result = await ProcessRunner.RunAsync(new ProcessSpec
                {
                    Executable = "docker",
                    Arguments = new[] { "volume", "rm", volume }
                }, cancellationToken);
                if (result != 0)
                    Logger.LogWarning("Failed to remove dangling docker volume {Volume}", volume);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
