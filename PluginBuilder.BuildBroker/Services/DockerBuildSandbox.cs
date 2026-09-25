using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;
using PluginBuilder.BuildBroker.Configuration;
using PluginBuilder.Builds;
using PluginBuilder.Builds.Services;
using PluginBuilder.Util;
using static PluginBuilder.Builds.Services.BuildPolicy;

namespace PluginBuilder.BuildBroker.Services;

public sealed class DockerBuildSandbox : IBuildSandbox
{
    private const int WorkerUserId = 10001;
    private const int CloneUserId = 10002;
    private const int ProxyUserId = 13;
    private const int WorkerPidLimit = 512;
    private const string ProxyResolverFileName = ".proxy-resolv.conf";
    private const string ProxyResolverConfiguration =
        "nameserver 1.1.1.1\n" +
        "nameserver 1.0.0.1\n" +
        "options timeout:1 attempts:2\n";
    private const string ProxyConfigurationFileName = ".proxy-squid.conf";
    // The broker owns the egress allowlist and mounts it into this pinned upstream image.
    public const string ProxyImage =
        "ubuntu/squid:6.6-24.04_edge@sha256:8a3baed477e2c282ab8aa5edad442f69873246964f225c5c2ae8364b6610963c";
    internal const string ProxyConfigurationPath = "/etc/squid/squid.conf";
    internal static readonly string ProxyConfiguration = LoadProxyConfiguration();
    private static readonly TimeSpan CloneTimeout = TimeSpan.FromMinutes(5);

    private readonly ILogger<DockerBuildSandbox> _logger;
    private readonly BuildExecutorOptions _options;
    private readonly ProcessRunner _processRunner;
    private readonly BuildExecutorState _executorState;
    private readonly BuildScratchCleaner _scratchCleaner;

    public DockerBuildSandbox(
        ILogger<DockerBuildSandbox> logger,
        BuildExecutorOptions options,
        ProcessRunner processRunner,
        BuildExecutorState executorState,
        BuildScratchCleaner scratchCleaner)
    {
        _logger = logger;
        _options = options;
        _processRunner = processRunner;
        _executorState = executorState;
        _scratchCleaner = scratchCleaner;
    }

    async Task<IPreparedBuild> IBuildSandbox.PrepareAsync(FullBuildId buildId, BuildInfo buildInfo, CancellationToken cancellationToken) =>
        await PrepareAsync(buildId, buildInfo, cancellationToken);

    public async Task<PreparedBuild> PrepareAsync(FullBuildId buildId, BuildInfo buildInfo, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // BrokerCoordinator validates and normalizes inputs before admission.

        var generation = _executorState.StopToken;
        var snapshot = _executorState.Snapshot;
        if (generation.IsCancellationRequested || snapshot.WorkerImageId is null || snapshot.ProxyImageId is null)
            throw new BuildServiceException("The isolated build executor is unavailable.");

        var prepared = new PreparedBuild(
            this,
            buildId,
            buildInfo,
            snapshot.WorkerImageId,
            snapshot.ProxyImageId,
            generation,
            cancellationToken);
        try
        {
            await prepared.PrepareAsync();
            return prepared;
        }
        catch
        {
            await prepared.DisposeAfterFailureAsync();
            throw;
        }
    }

    public static IReadOnlyList<string> CreateInternalNetworkArguments(string network, string label) =>
    [
        "network", "create", "--driver", "bridge", "--internal", "--ipv6=false",
        "--opt", "com.docker.network.bridge.inhibit_ipv4=true", "--label", label, network
    ];

    internal static readonly string ProxyUser = $"{ProxyUserId}:{ProxyUserId}";
    // Shared with the startup smoke test, which parses the proxy configuration in this layout.
    internal static readonly string[] ProxyTmpfsArguments =
    [
        "--tmpfs", $"/tmp:rw,noexec,nosuid,nodev,size=16m,mode=0700,uid={ProxyUserId},gid={ProxyUserId}",
        "--tmpfs", $"/run/squid:rw,noexec,nosuid,nodev,size=4m,mode=0700,uid={ProxyUserId},gid={ProxyUserId}",
        "--tmpfs", $"/var/log/squid:rw,noexec,nosuid,nodev,size=16m,mode=0700,uid={ProxyUserId},gid={ProxyUserId}",
        "--tmpfs", $"/var/spool/squid:rw,noexec,nosuid,nodev,size=16m,mode=0700,uid={ProxyUserId},gid={ProxyUserId}"
    ];

    // Shared with the startup smoke test, which parses the same mounted configuration.
    internal static string[] ProxyProgramArguments(string configurationFile) =>
    [
        "--mount", $"type=bind,source={configurationFile},target={ProxyConfigurationPath},readonly",
        "--entrypoint", "/usr/sbin/squid"
    ];

    public static IReadOnlyList<string> CreateProxyArguments(
        string container, string network, string resolverFile, string configurationFile, string image, string label,
        string runtime)
    {
        var arguments = DockerCli.HardenedContainer(container, label, runtime, network, "256m", 128,
            user: ProxyUser, cpus: "0.5", nofile: 1024, logs: ContainerLogs.Bounded);
        arguments.AddRange(ProxyTmpfsArguments);
        arguments.AddRange(["--mount", $"type=bind,source={resolverFile},target=/etc/resolv.conf,readonly"]);
        arguments.AddRange(ProxyProgramArguments(configurationFile));
        arguments.AddRange([image, "-N", "-f", ProxyConfigurationPath]);
        return arguments;
    }

    private static string LoadProxyConfiguration()
    {
        using var stream = typeof(DockerBuildSandbox).Assembly.GetManifestResourceStream("squid.conf")
            ?? throw new InvalidOperationException("The build proxy configuration is missing from the broker.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static void WriteReadOnlyFile(string path, string content)
    {
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    // Untrusted containers resolve nothing themselves: DNS points at an unroutable
    // address and every request goes through the build proxy.
    private static string[] IsolatedEgressArguments(string proxyIp)
    {
        var proxy = $"http://{proxyIp}:3128";
        return
        [
            "--dns", "192.0.2.1", "--dns-option", "timeout:1", "--dns-option", "attempts:1",
            "--env", $"HTTP_PROXY={proxy}", "--env", $"HTTPS_PROXY={proxy}",
            "--env", $"http_proxy={proxy}", "--env", $"https_proxy={proxy}",
            "--env", "ALL_PROXY=", "--env", "all_proxy=", "--env", "NO_PROXY=", "--env", "no_proxy="
        ];
    }

    public static IReadOnlyList<string> CreateWorkerArguments(
        string containerName,
        string internalNetwork,
        string proxyIp,
        string sourceDirectory,
        string workDirectory,
        string outputDirectory,
        string workerImageId,
        FullBuildId buildId,
        BuildInfo buildInfo,
        string runtime)
    {
        var arguments = DockerCli.HardenedContainer(containerName, $"{BuildExecutorDocker.ManagedResourceLabel}={buildId}",
            runtime, internalNetwork, "3g", WorkerPidLimit,
            user: $"{WorkerUserId}:{WorkerUserId}", cpus: "2", nofile: 4096, stopTimeout: true, logs: ContainerLogs.None);
        arguments.AddRange(IsolatedEgressArguments(proxyIp));
        arguments.AddRange(
        [
            "--tmpfs", $"/tmp:rw,exec,nosuid,nodev,size=512m,mode=0700,uid={WorkerUserId},gid={WorkerUserId}",
            "--mount", $"type=bind,source={sourceDirectory},target=/source,readonly",
            "--mount", $"type=bind,source={workDirectory},target=/build",
            "--mount", $"type=bind,source={outputDirectory},target=/out",
            "--env", "HOME=/build/home",
            "--env", "DOTNET_CLI_HOME=/build/home",
            "--env", "NUGET_PACKAGES=/build/nuget-packages",
            "--env", "GIT_CONFIG_NOSYSTEM=1"
        ]);

        if (!string.IsNullOrEmpty(buildInfo.PluginDir))
            arguments.AddRange(["--env", $"PLUGIN_DIR={buildInfo.PluginDir}"]);
        arguments.AddRange(["--env", $"BUILD_CONFIG={buildInfo.BuildConfig}"]);

        arguments.Add(workerImageId);
        return arguments;
    }

    // Opens one fixed-name file from trusted, quiescent staging: every container
    // that could write it has been removed, so the checked length cannot change.
    internal static FileStream OpenStagedFile(string directory, string fileName, long maxBytes, int bufferSize = 4096)
    {
        var staging = new DirectoryInfo(directory);
        if (!staging.Exists || staging.LinkTarget is not null || (staging.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new BuildServiceException("The trusted metadata staging directory is unavailable.");
        var file = new FileInfo(Path.Combine(staging.FullName, fileName));
        if (!file.Exists || file.LinkTarget is not null ||
            (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0 ||
            file.Length <= 0 || file.Length > maxBytes)
            throw new BuildServiceException($"Staged file '{fileName}' must be a nonempty regular file within its size limit.");
        var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.CanSeek && stream.Length == file.Length)
            return stream;
        stream.Dispose();
        throw new BuildServiceException($"Staged file '{fileName}' changed after staging.");
    }

    public sealed class PreparedBuild : IPreparedBuild
    {
        private readonly DockerBuildSandbox _owner;
        private readonly FullBuildId _buildId;
        private readonly BuildInfo _buildInfo;
        private readonly string _workerImageId;
        private readonly string _proxyImageId;
        private readonly CancellationToken _stopToken;
        private readonly CancellationTokenSource _leaseStopSource;
        private readonly string _resourceSuffix = Guid.NewGuid().ToString("N");
        private readonly List<(DockerResourceKind Kind, string Name)> _resources = [];
        private readonly HashSet<(DockerResourceKind Kind, string Name)> _ambiguousCreates = [];
        private bool _disposed;
        private bool _proxyFilesRemoved;

        internal PreparedBuild(
            DockerBuildSandbox owner,
            FullBuildId buildId,
            BuildInfo buildInfo,
            string workerImageId,
            string proxyImageId,
            CancellationToken generation,
            CancellationToken cancellationToken)
        {
            _owner = owner;
            _buildId = buildId;
            _buildInfo = buildInfo;
            _workerImageId = workerImageId;
            _proxyImageId = proxyImageId;
            WorkerContainer = $"pb-worker-{_resourceSuffix}";
            CloneContainer = $"pb-clone-{_resourceSuffix}";
            ProxyContainer = $"pb-proxy-{_resourceSuffix}";
            InternalNetwork = $"pb-internal-{_resourceSuffix}";
            EgressNetwork = $"pb-egress-{_resourceSuffix}";
            var scratchRoot = owner._options.BuildScratchRoot
                ?? throw new BuildServiceException("The isolated build scratch directory is not configured.");
            _leaseStopSource = CancellationTokenSource.CreateLinkedTokenSource(generation, cancellationToken);
            _stopToken = _leaseStopSource.Token;
            ScratchDirectory = Path.Combine(scratchRoot, $"pb-build-{_resourceSuffix}");
            SourceDirectory = Path.Combine(ScratchDirectory, "source");
            WorkDirectory = Path.Combine(ScratchDirectory, "work");
            OutputDirectory = Path.Combine(ScratchDirectory, "output");
            StagingDirectory = Path.Combine(ScratchDirectory, "staging");
            ProxyResolverFile = Path.Combine(WorkDirectory, ProxyResolverFileName);
            ProxyConfigurationFile = Path.Combine(WorkDirectory, ProxyConfigurationFileName);
        }

        public string WorkerContainer { get; }
        public string CloneContainer { get; }
        public string ProxyContainer { get; }
        public string InternalNetwork { get; }
        public string EgressNetwork { get; }
        public string ScratchDirectory { get; }
        public string SourceDirectory { get; }
        public string WorkDirectory { get; }
        public string OutputDirectory { get; }
        public string StagingDirectory { get; }
        private string ProxyResolverFile { get; }
        private string ProxyConfigurationFile { get; }

        internal async Task PrepareAsync()
        {
            CreateScratchDirectories();
            await InitializeScratchOwnership();
            await CreateResource(
                DockerResourceKind.Network,
                EgressNetwork,
                ["network", "create", "--driver", "bridge", "--ipv6=false", "--label", Label, EgressNetwork]);
            await CreateResource(
                DockerResourceKind.Network,
                InternalNetwork,
                CreateInternalNetworkArguments(InternalNetwork, Label));

            await CreateResource(
                DockerResourceKind.Container,
                ProxyContainer,
                CreateProxyArguments(ProxyContainer, EgressNetwork, _owner._options.DockerPath(ProxyResolverFile),
                    _owner._options.DockerPath(ProxyConfigurationFile), _proxyImageId, Label, _owner._options.Runtime));
            await RunDocker(
                ["network", "connect", InternalNetwork, ProxyContainer],
                "Connecting the build proxy to its internal network failed");
            await RunDocker(
                ["container", "start", ProxyContainer],
                "Starting the build proxy failed");

            var proxyState = await RunDockerForOutput(
                ["container", "inspect", "--format", "{{.State.Running}}", ProxyContainer],
                "Inspecting the build proxy failed");
            if (!string.Equals(proxyState.Trim(), "true", StringComparison.OrdinalIgnoreCase))
                throw new BuildServiceException("The isolated build proxy did not stay running.");

            await WaitForProxyReady();
            // The bind mounts keep both files alive for Squid after unlinking;
            // removing their scratch entries ensures the later untrusted worker cannot see them.
            RemoveProxyFiles();

            var proxyIp = await RunDockerForOutput(
                [
                    "container", "inspect", "--format",
                    $"{{{{(index .NetworkSettings.Networks \"{InternalNetwork}\").IPAddress}}}}",
                    ProxyContainer
                ],
                "Reading the build proxy address failed");
            proxyIp = proxyIp.Trim();
            if (!System.Net.IPAddress.TryParse(proxyIp, out var parsedProxyIp) ||
                parsedProxyIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                throw new BuildServiceException("The isolated build proxy has no valid internal IPv4 address.");

            _stopToken.ThrowIfCancellationRequested();
            await CloneSource(proxyIp);
            _stopToken.ThrowIfCancellationRequested();

            await CreateResource(
                DockerResourceKind.Container,
                WorkerContainer,
                CreateWorkerArguments(
                    WorkerContainer,
                    InternalNetwork,
                    proxyIp,
                    _owner._options.DockerPath(SourceDirectory),
                    _owner._options.DockerPath(WorkDirectory),
                    _owner._options.DockerPath(OutputDirectory),
                    _workerImageId,
                    _buildId,
                    _buildInfo,
                    _owner._options.Runtime));
        }

        public async Task<StagedBuildOutput> RunAndStageAsync(IOutputCapture buildOutput)
        {
            ThrowIfDisposed();
            _stopToken.ThrowIfCancellationRequested();

            int code;
            try
            {
                // The broker enforces the deadline independently of untrusted worker code.
                code = await DockerCli.RunAsync(_owner._processRunner, ["container", "start", "--attach", WorkerContainer],
                    _owner._options.WorkerExecutionTimeout, _stopToken, buildOutput, buildOutput);
            }
            catch (OperationCanceledException)
            {
                await RemoveWorkerOrThrow();
                if (_stopToken.IsCancellationRequested)
                    throw new BuildServiceException("The isolated build executor was stopped.");
                throw new PublicBuildException($"Plugin build timed out after {_owner._options.WorkerExecutionTimeout}.");
            }
            catch
            {
                await RemoveWorkerOrThrow();
                throw;
            }

            await RemoveWorkerOrThrow();
            if (code != 0)
            {
                _owner._logger.LogWarning(
                    "Build {BuildId} worker execution returned code {ExitCode}", _buildId, code);
                throw new BuildServiceException("Plugin build failed.");
            }

            await StageArtifacts();
            var buildEnvironmentJson = await ReadStagedFile("build-env.json", MaxBuildMetadataBytes);
            var manifestJson = await ReadStagedFile("manifest.json", MaxBuildMetadataBytes);

            JObject buildEnvironment;
            try
            {
                buildEnvironment = JObject.Parse(buildEnvironmentJson);
            }
            catch (Exception ex)
            {
                throw new BuildServiceException("The staged build metadata is invalid JSON: " + ex.Message);
            }

            var assemblyName = buildEnvironment["assemblyName"]?.Value<string>();
            if (!IsSafeAssemblyName(assemblyName))
                throw new BuildServiceException("The staged build metadata has an invalid assembly name.");
            if (!IsGitObjectId(buildEnvironment["gitCommit"]?.Value<string>()))
                throw new BuildServiceException("The staged build metadata has an invalid Git commit.");
            if (!TryReadTimestamp(buildEnvironment, "gitCommitDate") ||
                !TryReadTimestamp(buildEnvironment, "buildDate"))
                throw new BuildServiceException("The staged build metadata has an invalid timestamp.");
            if (!IsSha256Hex(buildEnvironment["buildHash"]?.Value<string>()))
                throw new BuildServiceException("The staged plugin artifact has an invalid SHA-256 digest.");

            // These fields describe the server-side request, not attacker-controlled output.
            buildEnvironment["gitRepository"] = _buildInfo.GitRepository;
            buildEnvironment["gitRef"] = _buildInfo.GitRef;
            buildEnvironment["pluginDir"] = _buildInfo.PluginDir;
            buildEnvironment["buildConfig"] = _buildInfo.BuildConfig;

            await RemoveProxyAndNetworksAsync();
            return new StagedBuildOutput(buildEnvironment, manifestJson, assemblyName, StagingDirectory);
        }

        public ValueTask DisposeAsync() => new(DisposeCoreAsync(throwOnFailure: true));

        internal Task DisposeAfterFailureAsync() => DisposeCoreAsync(throwOnFailure: false);

        private async Task DisposeCoreAsync(bool throwOnFailure)
        {
            if (_disposed)
                return;
            _disposed = true;
            try { await CleanupResources(throwOnFailure); }
            finally { _leaseStopSource.Dispose(); }
        }

        private string Label => $"{BuildExecutorDocker.ManagedResourceLabel}={_buildId}";

        private IReadOnlyList<string> CreateCloneArguments(string proxyIp)
        {
            var arguments = DockerCli.HardenedContainer(CloneContainer, Label, _owner._options.Runtime, InternalNetwork, "1g", 128,
                user: $"{CloneUserId}:{CloneUserId}", cpus: "1", nofile: 1024, stopTimeout: true, logs: ContainerLogs.None);
            arguments.AddRange(IsolatedEgressArguments(proxyIp));
            arguments.AddRange(
            [
                "--tmpfs", $"/tmp:rw,noexec,nosuid,nodev,size=128m,mode=0700,uid={CloneUserId},gid={CloneUserId}",
                "--mount", $"type=bind,source={_owner._options.DockerPath(SourceDirectory)},target=/source",
                "--env", $"GIT_REPO={_buildInfo.GitRepository}",
                "--entrypoint", "/clone-source.sh"
            ]);

            if (!string.IsNullOrEmpty(_buildInfo.GitRef))
                arguments.AddRange(["--env", $"GIT_REF={_buildInfo.GitRef}"]);

            arguments.Add(_workerImageId);
            return arguments;
        }

        private async Task CloneSource(string proxyIp)
        {
            await CreateResource(
                DockerResourceKind.Container,
                CloneContainer,
                CreateCloneArguments(proxyIp));

            int code;
            try
            {
                code = await DockerCli.RunAsync(_owner._processRunner, ["container", "start", "--attach", CloneContainer],
                    CloneTimeout, _stopToken);
            }
            catch (OperationCanceledException)
            {
                if (_stopToken.IsCancellationRequested)
                    throw new BuildServiceException("The isolated build executor was stopped.");
                throw new PublicBuildException("The repository checkout timed out.");
            }
            finally
            {
                await RemoveResourceOrThrow(
                    DockerResourceKind.Container,
                    CloneContainer,
                    "The isolated source checkout container could not be removed safely.");
            }

            if (code == 0)
                return;

            _owner._logger.LogWarning(
                "Repository checkout failed for build {BuildId} with exit code {ExitCode}",
                _buildId,
                code);
            throw new PublicBuildException("The repository checkout failed. Check the Git reference, repository access and submodules.");
        }

        private async Task StageArtifacts()
        {
            var stager = $"pb-stager-{_resourceSuffix}";
            var arguments = DockerCli.HardenedContainer(stager, Label, _owner._options.Runtime, "none", "512m", 64,
                user: $"{WorkerUserId}:{WorkerUserId}", cpus: "0.5", nofile: 256, logs: ContainerLogs.None);
            arguments.AddRange(
            [
                "--tmpfs", $"/tmp:rw,noexec,nosuid,nodev,size=16m,mode=0700,uid={WorkerUserId},gid={WorkerUserId}",
                "--mount", $"type=bind,source={_owner._options.DockerPath(OutputDirectory)},target=/untrusted-output,readonly",
                "--mount", $"type=bind,source={_owner._options.DockerPath(SourceDirectory)},target=/source,readonly",
                "--mount", $"type=bind,source={_owner._options.DockerPath(StagingDirectory)},target=/staging",
                "--entrypoint", "/stage-artifacts.sh",
                _workerImageId
            ]);
            const string runError = "Plugin artifact validation and staging failed";
            var diagnostics = new OutputCapture();
            try
            {
                await RunTrustedOneShot(stager, arguments, runError,
                    "The trusted artifact staging container could not be removed safely.", diagnostics);
            }
            // Only the run step is public: create and removal failures name internal resources.
            catch (BuildServiceException error) when (!_stopToken.IsCancellationRequested &&
                                                      error.Message.StartsWith(runError, StringComparison.Ordinal))
            {
                // Only this trusted script's stdout carries public diagnostics; stderr stays private.
                var diagnostic = diagnostics.Lines.LastOrDefault();
                if (diagnostic is { Length: <= 512 } && !diagnostic.Any(char.IsControl) &&
                    diagnostic.StartsWith("Artifact staging rejected: ", StringComparison.Ordinal))
                    throw new PublicBuildException(diagnostic);
                // The run step's message is our fixed description plus a fixed suffix.
                throw new PublicBuildException(error.Message);
            }
        }

        private async Task RunTrustedOneShot(string name, IReadOnlyList<string> createArguments, string runError,
            string removeError, IOutputCapture? output = null)
        {
            await CreateResource(DockerResourceKind.Container, name, createArguments);
            try
            {
                await RunDocker(["container", "start", "--attach", name], runError, output);
            }
            finally
            {
                await RemoveResourceOrThrow(DockerResourceKind.Container, name, removeError);
            }
        }

        private async Task<string> ReadStagedFile(string file, int maxBytes)
        {
            // Only fixed filenames are read from trusted, quiescent staging:
            // the worker never mounts it and the stager has already been removed.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopToken);
            timeout.CancelAfter(_owner._options.DockerOperationTimeout);
            try
            {
                timeout.Token.ThrowIfCancellationRequested();
                await using var stream = OpenStagedFile(StagingDirectory, file, maxBytes);
                var bytes = new byte[(int)stream.Length];
                await stream.ReadExactlyAsync(bytes, timeout.Token);
                // Preserve the BOM detection of the former Docker stdout reader,
                // without allowing the text decoder to read past the byte limit.
                using var reader = new StreamReader(new MemoryStream(bytes, writable: false), Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch (OperationCanceledException)
            {
                throw new BuildServiceException(_stopToken.IsCancellationRequested
                    ? "The isolated build executor was stopped."
                    : $"Reading staged file '{file}' timed out.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new BuildServiceException($"Reading staged file '{file}' failed.");
            }
        }

        private async Task RemoveProxyAndNetworksAsync()
        {
            var kinds = new[]
            {
                (DockerResourceKind.Container, ProxyContainer),
                (DockerResourceKind.Network, InternalNetwork),
                (DockerResourceKind.Network, EgressNetwork)
            };

            List<string> failures = [];
            foreach (var (kind, name) in kinds)
            {
                if (!await RemoveResource(kind, name))
                    failures.Add($"{kind}:{name}");
            }

            if (failures.Count == 0)
                return;

            _owner._executorState.MarkUnavailable("An isolated build resource could not be cleaned up");
            throw new BuildServiceException("The isolated build executor failed to clean up build resources.");
        }

        private async Task RemoveWorkerOrThrow()
        {
            await RemoveResourceOrThrow(
                DockerResourceKind.Container,
                WorkerContainer,
                "The isolated build worker could not be removed safely.");
        }

        private async Task CreateResource(
            DockerResourceKind kind,
            string name,
            IReadOnlyList<string> arguments)
        {
            // docker may create a resource before the client observes a timeout or
            // transport failure. Track it first so every ambiguous create is cleaned.
            _resources.Add((kind, name));
            try
            {
                await RunDocker(arguments, $"Creating build {kind.ToString().ToLowerInvariant()} '{name}' failed");
            }
            catch
            {
                // Even a non-zero docker client exit cannot prove the daemon did
                // not accept the create request before returning an error.
                _ambiguousCreates.Add((kind, name));
                _owner._executorState.MarkUnavailable(
                    "A docker create operation had an ambiguous result");
                throw;
            }
        }

        private async Task WaitForProxyReady()
        {
            const string readinessProbe =
                "for attempt in $(seq 1 50); do " +
                "if exec 3<>/dev/tcp/127.0.0.1/3128; then exec 3>&-; exec 3<&-; exit 0; fi; " +
                "sleep 0.1; done; exit 1";
            await RunDocker(
                ["container", "exec", ProxyContainer, "/bin/bash", "-c", readinessProbe],
                "The isolated build proxy did not become ready");
        }

        private void CreateScratchDirectories()
        {
            var configuredRoot = Path.GetFullPath(_owner._options.BuildScratchRoot!);
            if (!Directory.Exists(configuredRoot) ||
                (File.GetAttributes(configuredRoot) & FileAttributes.ReparsePoint) != 0)
                throw new BuildServiceException("The isolated build scratch directory is unavailable.");
            var expectedPrefix = configuredRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullScratchPath = Path.GetFullPath(ScratchDirectory);
            if (!fullScratchPath.StartsWith(expectedPrefix, StringComparison.Ordinal))
                throw new BuildServiceException("The isolated build scratch path is invalid.");

            // Track before the first write so partial creation is still reconciled.
            _resources.Add((DockerResourceKind.Directory, ScratchDirectory));
            Directory.CreateDirectory(SourceDirectory);
            Directory.CreateDirectory(WorkDirectory);
            Directory.CreateDirectory(OutputDirectory);
            Directory.CreateDirectory(StagingDirectory);
            // Docker's --dns still routes through 127.0.0.11 on user-defined bridges,
            // which runsc cannot reach. A direct, read-only resolver file avoids it.
            WriteReadOnlyFile(ProxyResolverFile, ProxyResolverConfiguration);
            WriteReadOnlyFile(ProxyConfigurationFile, ProxyConfiguration);
        }

        private void RemoveProxyFiles()
        {
            try
            {
                File.Delete(ProxyResolverFile);
                File.Delete(ProxyConfigurationFile);
                _proxyFilesRemoved = true;
            }
            catch (Exception ex)
            {
                _owner._logger.LogWarning(ex, "Failed to remove the temporary proxy configuration");
                throw new BuildServiceException("The temporary proxy configuration could not be removed safely.");
            }
        }

        private Task InitializeScratchOwnership()
        {
            var initializer = $"pb-scratch-init-{_resourceSuffix}";
            // The PID budget must also cover gVisor's sandbox and gofer threads.
            var arguments = DockerCli.HardenedContainer(initializer, Label, _owner._options.Runtime, "none", "64m", 64,
                user: "0:0", capAdd: ["CHOWN"], nofile: 64, logs: ContainerLogs.None);
            arguments.AddRange(
            [
                "--mount", $"type=bind,source={_owner._options.DockerPath(ScratchDirectory)},target=/scratch",
                "--entrypoint", "/bin/sh",
                _workerImageId,
                "-c",
                "chmod 0755 /scratch/source && " +
                "chmod 0700 /scratch/work /scratch/output /scratch/staging && " +
                $"chown {CloneUserId}:{CloneUserId} /scratch/source && " +
                $"chown {WorkerUserId}:{WorkerUserId} /scratch/work /scratch/output /scratch/staging"
            ]);
            return RunTrustedOneShot(initializer, arguments, "Initializing isolated build scratch ownership failed",
                "The trusted scratch initializer container could not be removed safely.");
        }

        private async Task RemoveResourceOrThrow(
            DockerResourceKind kind,
            string name,
            string safeError)
        {
            if (await RemoveResource(kind, name))
                return;

            _owner._executorState.MarkUnavailable("An isolated build resource could not be removed");
            throw new BuildServiceException(safeError);
        }

        private async Task RunDocker(
            IReadOnlyList<string> arguments,
            string safeError,
            IOutputCapture? outputCapture = null)
        {
            OutputCapture error = new();
            int code;
            try
            {
                code = await DockerCli.RunAsync(_owner._processRunner, arguments, _owner._options.DockerOperationTimeout, _stopToken, outputCapture, error);
            }
            catch (OperationCanceledException)
            {
                if (_stopToken.IsCancellationRequested)
                    throw new BuildServiceException("The isolated build executor was stopped.");
                throw new BuildServiceException(safeError + " (operation timed out).");
            }
            catch (Exception ex)
            {
                // Once the docker client has been started, a local transport error
                // cannot prove that the daemon did not accept the create request.
                _owner._logger.LogWarning(ex, "{SafeError}: docker client failed", safeError);
                throw new BuildServiceException(safeError + " (docker client failed).");
            }

            if (code != 0)
            {
                _owner._logger.LogWarning("{SafeError}: {DockerError}", safeError, error.ToString().Trim());
                throw new BuildServiceException(safeError + ".");
            }
        }

        private async Task<string> RunDockerForOutput(IReadOnlyList<string> arguments, string safeError)
        {
            OutputCapture output = new();
            await RunDocker(arguments, safeError, output);
            return output.ToString();
        }

        private async Task<bool> RemoveResource(DockerResourceKind kind, string name)
        {
            var index = _resources.FindLastIndex(resource => resource.Kind == kind && resource.Name == name);
            if (index < 0)
                return true;

            if (kind == DockerResourceKind.Directory)
            {
                if (await _owner._scratchCleaner.TryDeleteAsync(
                        name,
                        _workerImageId,
                        CancellationToken.None))
                {
                    _resources.RemoveAt(index);
                    return true;
                }

                return false;
            }
            if (!await DockerCli.TryRemoveAsync(
                    _owner._processRunner, _owner._logger, kind.ToString().ToLowerInvariant(), name,
                    _ambiguousCreates.Contains((kind, name))))
                return false;

            _ambiguousCreates.Remove((kind, name));
            _resources.RemoveAt(index);
            return true;
        }

        private async Task CleanupResources(bool throwOnFailure)
        {
            List<string> failures = [];
            var resources = _resources.AsEnumerable().Reverse().ToArray();
            foreach (var resource in resources.Where(resource => resource.Kind != DockerResourceKind.Directory))
            {
                if (!await RemoveResource(resource.Kind, resource.Name))
                    failures.Add($"{resource.Kind}:{resource.Name}");
            }

            if (failures.Count == 0 && !_proxyFilesRemoved)
            {
                try
                {
                    RemoveProxyFiles();
                }
                catch (BuildServiceException)
                {
                    failures.Add($"File:{ProxyResolverFile}");
                    failures.Add($"File:{ProxyConfigurationFile}");
                }
            }

            // Never traverse attacker-controlled files while an untrusted container
            // may still be alive and modifying them.
            if (failures.Count == 0)
            {
                foreach (var resource in resources.Where(resource => resource.Kind == DockerResourceKind.Directory))
                {
                    if (!await RemoveResource(resource.Kind, resource.Name))
                        failures.Add($"{resource.Kind}:{resource.Name}");
                }
            }

            if (failures.Count == 0)
                return;

            _owner._executorState.MarkUnavailable("An isolated build resource could not be cleaned up");
            if (throwOnFailure)
                throw new BuildServiceException("The isolated build executor failed to clean up build resources.");
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        private static bool TryReadTimestamp(JObject buildEnvironment, string property)
        {
            var value = buildEnvironment[property]?.Value<string>();
            return value is { Length: > 0 and <= 64 } &&
                   DateTimeOffset.TryParse(
                       value,
                       CultureInfo.InvariantCulture,
                       DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                       out _);
        }

    }

    private enum DockerResourceKind
    {
        Container,
        Network,
        Directory
    }
}
