namespace PluginBuilder.HostedServices
{
    public class DockerStartupException : Exception
    {
        public DockerStartupException(string message) : base(message)
        {

        }
    }
    public class DockerStartupHostedService : IHostedService
    {
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
            Logger.LogInformation("Building the PluginBuilder docker image");
            var result = await ProcessRunner.RunAsync(new ProcessSpec()
            {
                Executable = "docker",
                Arguments = new[]
                {
                    "build",
                    "-f", "PluginBuilder.Dockerfile",
                    "-t", "plugin-builder",
                    "."
                },
                WorkingDirectory = ContentRootPath
            }, cancellationToken);
            if (result != 0)
                throw new DockerStartupException("The build of PluginBuilder.Dockerfile failed");
            var output = new OutputCapture();
            await ProcessRunner.RunAsync(new ProcessSpec()
            {
                Executable = "docker",
                Arguments = new[]
                {
                    "volume", "ls",
                    "-f", "label=BTCPAY_PLUGIN_BUILD",
                    "--format", "{{ .Name }}"
                },
                OutputCapture = output
            }, cancellationToken);
            if (result != 0)
                throw new DockerStartupException("docker volume ls failed");
            if (output.Lines.Any())
            {
                Logger.LogInformation("Cleaning dangling volumes");
                var args = new List<string>();
                args.Add("volume");
                args.Add("rm");
                args.AddRange(output.Lines);
                await ProcessRunner.RunAsync(new ProcessSpec()
                {
                    Executable = "docker",
                    Arguments = args.ToArray()
                }, cancellationToken);
                if (result != 0)
                    throw new DockerStartupException("docker volume rm failed");
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
