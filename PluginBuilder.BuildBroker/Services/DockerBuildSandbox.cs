using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
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
    private const string ProxyResolverFileName = ".proxy-resolv.conf";
    private const string ProxyResolverConfiguration =
        "nameserver 1.1.1.1\n" +
        "nameserver 1.0.0.1\n" +
        "options timeout:1 attempts:2\n";
    private static readonly TimeSpan DockerOperationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CloneTimeout = TimeSpan.FromMinutes(5);
    private static readonly Regex SafeAssemblyName = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex GitObjectId = new(
        "^(?:[0-9a-f]{40}|[0-9a-f]{64})$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

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
        buildInfo.GitRepository = NormalizeRepositoryUrl(buildInfo.GitRepository);
        buildInfo.BuildConfig = string.IsNullOrEmpty(buildInfo.BuildConfig)
            ? "Release"
            : buildInfo.BuildConfig;
        ValidateBuildInputs(buildInfo.GitRef, buildInfo.PluginDir, buildInfo.BuildConfig);

        var snapshot = _executorState.Snapshot;
        if (!snapshot.IsReady || snapshot.WorkerImageId is null || snapshot.ProxyImageId is null)
            throw new BuildServiceException("The isolated build executor is unavailable.");

        var prepared = new PreparedBuild(
            this,
            buildId,
            buildInfo,
            snapshot.WorkerImageId,
            snapshot.ProxyImageId,
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

    public static IReadOnlyList<string> CreateProxyArguments(
        string container, string network, string resolverFile, string image, string label, bool useRunc = false) =>
    [
        "container", "create",
        "--name", container,
        "--label", label,
        "--runtime", useRunc ? "runc" : "runsc",
        "--network", network,
        "--read-only",
        "--user", $"{ProxyUserId}:{ProxyUserId}",
        "--cap-drop", "ALL",
        "--security-opt", "no-new-privileges:true",
        "--memory", "256m",
        "--memory-swap", "256m",
        "--cpus", "0.5",
        "--pids-limit", "128",
        "--ulimit", "nofile=1024:1024",
        "--tmpfs", $"/tmp:rw,noexec,nosuid,nodev,size=16m,mode=0700,uid={ProxyUserId},gid={ProxyUserId}",
        "--tmpfs", $"/run/squid:rw,noexec,nosuid,nodev,size=4m,mode=0700,uid={ProxyUserId},gid={ProxyUserId}",
        "--tmpfs", $"/var/log/squid:rw,noexec,nosuid,nodev,size=16m,mode=0700,uid={ProxyUserId},gid={ProxyUserId}",
        "--tmpfs", $"/var/spool/squid:rw,noexec,nosuid,nodev,size=16m,mode=0700,uid={ProxyUserId},gid={ProxyUserId}",
        "--mount", $"type=bind,source={resolverFile},target=/etc/resolv.conf,readonly",
        "--log-opt", "max-size=1m",
        "--log-opt", "max-file=1",
        image
    ];

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
        bool useRunc = false)
    {
        var proxy = $"http://{proxyIp}:3128";
        List<string> arguments =
        [
            "container", "create",
            "--name", containerName,
            "--label", $"{BuildExecutorDocker.ManagedResourceLabel}={buildId}",
            "--network", internalNetwork,
            "--dns", "192.0.2.1",
            "--dns-option", "timeout:1",
            "--dns-option", "attempts:1",
            "--runtime", useRunc ? "runc" : "runsc",
            "--read-only",
            "--user", $"{WorkerUserId}:{WorkerUserId}",
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges:true",
            "--memory", "3g",
            "--memory-swap", "3g",
            "--cpus", "2",
            "--pids-limit", WorkerPidLimit.ToString(CultureInfo.InvariantCulture),
            "--ulimit", "nofile=4096:4096",
            "--stop-timeout", "5",
            "--log-driver", "none",
            "--tmpfs", $"/tmp:rw,exec,nosuid,nodev,size=512m,mode=0700,uid={WorkerUserId},gid={WorkerUserId}",
            "--mount", $"type=bind,source={sourceDirectory},target=/source,readonly",
            "--mount", $"type=bind,source={workDirectory},target=/build",
            "--mount", $"type=bind,source={outputDirectory},target=/out",
            "--env", $"HTTP_PROXY={proxy}",
            "--env", $"HTTPS_PROXY={proxy}",
            "--env", $"http_proxy={proxy}",
            "--env", $"https_proxy={proxy}",
            "--env", "ALL_PROXY=",
            "--env", "all_proxy=",
            "--env", "NO_PROXY=",
            "--env", "no_proxy=",
            "--env", "HOME=/build/home",
            "--env", "DOTNET_CLI_HOME=/build/home",
            "--env", "NUGET_PACKAGES=/build/nuget-packages",
            "--env", "GIT_CONFIG_NOSYSTEM=1"
        ];

        if (!string.IsNullOrEmpty(buildInfo.PluginDir))
            arguments.AddRange(["--env", $"PLUGIN_DIR={buildInfo.PluginDir}"]);
        if (!string.IsNullOrEmpty(buildInfo.BuildConfig))
            arguments.AddRange(["--env", $"BUILD_CONFIG={buildInfo.BuildConfig}"]);

        arguments.Add(workerImageId);
        return arguments;
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
        private bool _proxyResolverFileRemoved;

        internal PreparedBuild(
            DockerBuildSandbox owner,
            FullBuildId buildId,
            BuildInfo buildInfo,
            string workerImageId,
            string proxyImageId,
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
            _leaseStopSource = CancellationTokenSource.CreateLinkedTokenSource(owner._executorState.StopToken, cancellationToken);
            _stopToken = _leaseStopSource.Token;
            ScratchDirectory = Path.Combine(scratchRoot, $"pb-build-{_resourceSuffix}");
            SourceDirectory = Path.Combine(ScratchDirectory, "source");
            WorkDirectory = Path.Combine(ScratchDirectory, "work");
            OutputDirectory = Path.Combine(ScratchDirectory, "output");
            StagingDirectory = Path.Combine(ScratchDirectory, "staging");
            ProxyResolverFile = Path.Combine(WorkDirectory, ProxyResolverFileName);
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
                    _proxyImageId, Label, _owner._options.UseRunc));
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
            // The bind mount keeps the resolver file alive for Squid after unlinking;
            // removing its scratch entry ensures the later untrusted worker cannot see it.
            RemoveProxyResolverFile();

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

            var current = _owner._executorState.Snapshot;
            if (!current.IsReady || current.WorkerImageId != _workerImageId || current.ProxyImageId != _proxyImageId)
                throw new BuildServiceException("The isolated build executor became unavailable.");

            await CloneSource(proxyIp);

            current = _owner._executorState.Snapshot;
            if (!current.IsReady || current.WorkerImageId != _workerImageId || current.ProxyImageId != _proxyImageId)
                throw new BuildServiceException("The isolated build executor became unavailable.");

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
                    _owner._options.UseRunc));
        }

        public async Task<StagedBuildOutput> RunAndStageAsync(IOutputCapture buildOutput)
        {
            ThrowIfDisposed();
            var current = _owner._executorState.Snapshot;
            if (!current.IsReady || current.WorkerImageId != _workerImageId || current.ProxyImageId != _proxyImageId)
                throw new BuildServiceException("The isolated build executor became unavailable.");

            int code;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopToken))
            {
                // The broker enforces the deadline independently of untrusted worker code.
                timeout.CancelAfter(_owner._options.WorkerExecutionTimeout);
                try
                {
                    code = await _owner._processRunner.RunAsync(new ProcessSpec
                    {
                        Executable = "docker",
                        Arguments = ["container", "start", "--attach", WorkerContainer],
                        OutputCapture = buildOutput,
                        ErrorCapture = buildOutput
                    }, timeout.Token);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    await RemoveWorkerOrThrow();
                    if (_stopToken.IsCancellationRequested)
                        throw new BuildServiceException("The isolated build executor was stopped.");
                    throw new BuildServiceException($"Plugin build timed out after {_owner._options.WorkerExecutionTimeout}.");
                }
                catch
                {
                    await RemoveWorkerOrThrow();
                    throw;
                }
            }

            if (code != 0)
                _owner._logger.LogWarning(
                    "Build {BuildId} worker execution returned code {ExitCode}", _buildId, code);
            await RemoveWorkerOrThrow();

            if (code != 0)
                throw new BuildServiceException("Plugin build failed.");

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
            if (assemblyName is null || !SafeAssemblyName.IsMatch(assemblyName))
                throw new BuildServiceException("The staged build metadata has an invalid assembly name.");
            var gitCommit = buildEnvironment["gitCommit"]?.Value<string>();
            if (gitCommit is null || !GitObjectId.IsMatch(gitCommit))
                throw new BuildServiceException("The staged build metadata has an invalid Git commit.");
            if (!TryReadTimestamp(buildEnvironment, "gitCommitDate") ||
                !TryReadTimestamp(buildEnvironment, "buildDate"))
                throw new BuildServiceException("The staged build metadata has an invalid timestamp.");
            var trustedHash = buildEnvironment["buildHash"]?.Value<string>();
            if (trustedHash is null || !Regex.IsMatch(trustedHash, "\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant))
                throw new BuildServiceException("The staged plugin artifact has an invalid SHA-256 digest.");

            // These fields describe the server-side request, not attacker-controlled output.
            buildEnvironment["gitRepository"] = _buildInfo.GitRepository;
            buildEnvironment["gitRef"] = _buildInfo.GitRef;
            buildEnvironment["pluginDir"] = _buildInfo.PluginDir;
            buildEnvironment["buildConfig"] = _buildInfo.BuildConfig ?? "Release";

            await RemoveProxyAndNetworksAsync();
            return new StagedBuildOutput(buildEnvironment, manifestJson, assemblyName, StagingDirectory);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;
            try { await CleanupResources(throwOnFailure: true); }
            finally { _leaseStopSource.Dispose(); }
        }

        internal async Task DisposeAfterFailureAsync()
        {
            if (_disposed)
                return;
            _disposed = true;
            try { await CleanupResources(throwOnFailure: false); }
            finally { _leaseStopSource.Dispose(); }
        }

        private string Label => $"{BuildExecutorDocker.ManagedResourceLabel}={_buildId}";

        private IReadOnlyList<string> CreateCloneArguments(string proxyIp)
        {
            var proxy = $"http://{proxyIp}:3128";
            List<string> arguments =
            [
                "container", "create",
                "--name", CloneContainer,
                "--label", Label,
                "--runtime", _owner._options.Runtime,
                "--network", InternalNetwork,
                "--dns", "192.0.2.1",
                "--dns-option", "timeout:1",
                "--dns-option", "attempts:1",
                "--read-only",
                "--user", $"{CloneUserId}:{CloneUserId}",
                "--cap-drop", "ALL",
                "--security-opt", "no-new-privileges:true",
                "--memory", "1g",
                "--memory-swap", "1g",
                "--cpus", "1",
                "--pids-limit", "128",
                "--ulimit", "nofile=1024:1024",
                "--stop-timeout", "5",
                "--log-driver", "none",
                "--tmpfs", $"/tmp:rw,noexec,nosuid,nodev,size=128m,mode=0700,uid={CloneUserId},gid={CloneUserId}",
                "--mount", $"type=bind,source={_owner._options.DockerPath(SourceDirectory)},target=/source",
                "--env", $"HTTP_PROXY={proxy}",
                "--env", $"HTTPS_PROXY={proxy}",
                "--env", $"http_proxy={proxy}",
                "--env", $"https_proxy={proxy}",
                "--env", "ALL_PROXY=",
                "--env", "all_proxy=",
                "--env", "NO_PROXY=",
                "--env", "no_proxy=",
                "--env", $"GIT_REPO={_buildInfo.GitRepository}",
                "--entrypoint", "/clone-source.sh"
            ];

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

            OutputCapture output = new();
            int code;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopToken);
            timeout.CancelAfter(CloneTimeout);
            try
            {
                code = await _owner._processRunner.RunAsync(new ProcessSpec
                {
                    Executable = "docker",
                    Arguments = ["container", "start", "--attach", CloneContainer],
                    OutputCapture = output,
                    ErrorCapture = output
                }, timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                if (_stopToken.IsCancellationRequested)
                    throw new BuildServiceException("The isolated build executor was stopped.");
                throw new BuildServiceException("The repository checkout timed out.");
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
            throw new BuildServiceException("The repository checkout failed.");
        }

        private async Task StageArtifacts()
        {
            var stager = $"pb-stager-{_resourceSuffix}";
            await CreateResource(
                DockerResourceKind.Container,
                stager,
                [
                    "container", "create",
                    "--name", stager,
                    "--label", Label,
                    "--runtime", _owner._options.Runtime,
                    "--network", "none",
                    "--read-only",
                    "--user", $"{WorkerUserId}:{WorkerUserId}",
                    "--cap-drop", "ALL",
                    "--security-opt", "no-new-privileges:true",
                    "--memory", "512m",
                    "--memory-swap", "512m",
                    "--cpus", "0.5",
                    "--pids-limit", "64",
                    "--ulimit", "nofile=256:256",
                    "--log-driver", "none",
                    "--tmpfs", $"/tmp:rw,noexec,nosuid,nodev,size=16m,mode=0700,uid={WorkerUserId},gid={WorkerUserId}",
                    "--mount", $"type=bind,source={_owner._options.DockerPath(OutputDirectory)},target=/untrusted-output,readonly",
                    "--mount", $"type=bind,source={_owner._options.DockerPath(SourceDirectory)},target=/source,readonly",
                    "--mount", $"type=bind,source={_owner._options.DockerPath(StagingDirectory)},target=/staging",
                    "--entrypoint", "/stage-artifacts.sh",
                    _workerImageId
                ]);

            try
            {
                await RunDocker(
                    ["container", "start", "--attach", stager],
                    "Plugin artifact validation and staging failed");
            }
            finally
            {
                await RemoveResourceOrThrow(
                    DockerResourceKind.Container,
                    stager,
                    "The trusted artifact staging container could not be removed safely.");
            }
        }

        private async Task<string> ReadStagedFile(string file, int maxBytes)
        {
            // Only fixed filenames are read from trusted, quiescent staging:
            // the worker never mounts it and the stager has already been removed.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopToken);
            timeout.CancelAfter(DockerOperationTimeout);
            try
            {
                timeout.Token.ThrowIfCancellationRequested();
                var directory = new DirectoryInfo(StagingDirectory);
                if (!directory.Exists || directory.LinkTarget is not null ||
                    (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new BuildServiceException("The trusted metadata staging directory is unavailable.");

                var metadata = new FileInfo(Path.Combine(directory.FullName, file));
                if (!metadata.Exists || metadata.LinkTarget is not null ||
                    (metadata.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0 ||
                    metadata.Length <= 0 || metadata.Length > maxBytes)
                    throw new BuildServiceException($"Staged file '{file}' must be a nonempty regular file within its size limit.");

                await using var stream = new FileStream(metadata.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (!stream.CanSeek || stream.Length != metadata.Length)
                    throw new BuildServiceException($"Staged file '{file}' changed after staging.");

                var bytes = new byte[(int)metadata.Length];
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
            File.WriteAllText(ProxyResolverFile, ProxyResolverConfiguration);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(
                    ProxyResolverFile,
                    UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        private void RemoveProxyResolverFile()
        {
            try
            {
                File.Delete(ProxyResolverFile);
                _proxyResolverFileRemoved = true;
            }
            catch (Exception ex)
            {
                _owner._logger.LogWarning(ex, "Failed to remove the temporary proxy resolver configuration");
                throw new BuildServiceException("The temporary proxy resolver configuration could not be removed safely.");
            }
        }

        private async Task InitializeScratchOwnership()
        {
            var initializer = $"pb-scratch-init-{_resourceSuffix}";
            await CreateResource(
                DockerResourceKind.Container,
                initializer,
                [
                    "container", "create",
                    "--name", initializer,
                    "--label", Label,
                    "--runtime", _owner._options.Runtime,
                    "--network", "none",
                    "--read-only",
                    "--user", "0:0",
                    "--cap-drop", "ALL",
                    "--cap-add", "CHOWN",
                    "--security-opt", "no-new-privileges:true",
                    "--memory", "64m",
                    "--memory-swap", "64m",
                    // The PID budget must also cover gVisor's sandbox and gofer threads.
                    "--pids-limit", "64",
                    "--ulimit", "nofile=64:64",
                    "--log-driver", "none",
                    "--mount", $"type=bind,source={_owner._options.DockerPath(ScratchDirectory)},target=/scratch",
                    "--entrypoint", "/bin/sh",
                    _workerImageId,
                    "-c",
                    "chmod 0755 /scratch/source && " +
                    "chmod 0700 /scratch/work /scratch/output /scratch/staging && " +
                    $"chown {CloneUserId}:{CloneUserId} /scratch/source && " +
                    $"chown {WorkerUserId}:{WorkerUserId} /scratch/work /scratch/output /scratch/staging"
                ]);
            try
            {
                await RunDocker(
                    ["container", "start", "--attach", initializer],
                    "Initializing isolated build scratch ownership failed");
            }
            finally
            {
                await RemoveResourceOrThrow(
                    DockerResourceKind.Container,
                    initializer,
                    "The trusted scratch initializer container could not be removed safely.");
            }
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
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopToken);
            timeout.CancelAfter(DockerOperationTimeout);
            int code;
            try
            {
                code = await _owner._processRunner.RunAsync(new ProcessSpec
                {
                    Executable = "docker",
                    Arguments = arguments,
                    OutputCapture = outputCapture,
                    ErrorCapture = error
                }, timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
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
            if (!await DockerResourceCleanup.TryRemoveAsync(
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

            if (failures.Count == 0 && !_proxyResolverFileRemoved)
            {
                try
                {
                    RemoveProxyResolverFile();
                }
                catch (BuildServiceException)
                {
                    failures.Add($"File:{ProxyResolverFile}");
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
