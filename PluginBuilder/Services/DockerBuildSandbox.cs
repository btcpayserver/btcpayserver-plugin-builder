using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using PluginBuilder.Configuration;
using PluginBuilder.Util;

namespace PluginBuilder.Services;

public sealed class DockerBuildSandbox
{
    public const int MaxConcurrentBuilds = 2;
    public const int MaxBuildMetadataBytes = 1024 * 1024;
    public const int MaxBuildLogBytes = 10 * 1024 * 1024;
    public const int MaxBuildLogLineBytes = 64 * 1024;
    public const int MaxBuildLogLines = 10_000;
    public const int MaxCloneLogBytes = 1024 * 1024;
    public const int MaxCloneLogLines = 2_000;

    private const int WorkerUserId = 10001;
    private const int CloneUserId = 10002;
    private const int ProxyUserId = 13;
    private const string ProxyResolverFileName = ".proxy-resolv.conf";
    private const string ProxyResolverConfiguration =
        "nameserver 1.1.1.1\n" +
        "nameserver 1.0.0.1\n" +
        "options timeout:1 attempts:2\n";
    private const int MaxRepositoryUrlCharacters = 2048;
    private const int MaxGitRefCharacters = 255;
    private const int MaxPluginDirectoryCharacters = 1024;
    private static readonly TimeSpan DockerOperationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CloneTimeout = TimeSpan.FromMinutes(5);
    private static readonly Regex SafeAssemblyName = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex GitObjectId = new(
        "^(?:[0-9a-f]{40}|[0-9a-f]{64})$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly ILogger<DockerBuildSandbox> _logger;
    private readonly PluginBuilderOptions _options;
    private readonly ProcessRunner _processRunner;
    private readonly BuildExecutorState _executorState;
    private readonly BuildScratchCleaner _scratchCleaner;
    private readonly bool[] _scratchSlotsInUse = new bool[MaxConcurrentBuilds];

    public static string ScratchSlotPath(string scratchRoot, int slot) => Path.Combine(scratchRoot, $"slot-{slot}");

    private int AcquireScratchSlot()
    {
        lock (_scratchSlotsInUse)
        {
            for (var slot = 0; slot < _scratchSlotsInUse.Length; slot++)
            {
                if (_scratchSlotsInUse[slot])
                    continue;
                var path = ScratchSlotPath(_options.BuildScratchRoot!, slot);
                if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new BuildServiceException("The isolated build scratch slot is unavailable.");
                _scratchSlotsInUse[slot] = true;
                return slot;
            }
        }
        throw new BuildServiceException("All isolated build scratch slots are in use.");
    }

    private void ReleaseScratchSlot(int slot)
    {
        lock (_scratchSlotsInUse)
            _scratchSlotsInUse[slot] = false;
    }

    public DockerBuildSandbox(
        ILogger<DockerBuildSandbox> logger,
        PluginBuilderOptions options,
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

    public async Task<PreparedBuild> PrepareAsync(FullBuildId buildId, BuildInfo buildInfo)
    {
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
            snapshot.ProxyImageId);
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

    public static void ValidateRepositoryUrl(string repository)
    {
        _ = NormalizeRepositoryUrl(repository);
    }

    public static string NormalizeRepositoryUrl(string repository)
    {
        if (string.IsNullOrEmpty(repository) || repository.Length > MaxRepositoryUrlCharacters ||
            !Uri.TryCreate(repository, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            (!uri.IsDefaultPort && uri.Port != 443) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 ||
            uri.IdnHost is not ("github.com" or "www.github.com" or "gitlab.com" or "www.gitlab.com"))
        {
            throw new BuildServiceException(
                "Git repository must be an anonymous HTTPS URL on github.com or gitlab.com.");
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        var pathSegments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (pathSegments.Length < 2 ||
            !Regex.IsMatch(path, "^/[A-Za-z0-9._/-]+$", RegexOptions.CultureInvariant) ||
            path.Contains("//", StringComparison.Ordinal) ||
            pathSegments.Any(segment => segment is "." or ".."))
        {
            throw new BuildServiceException(
                "Git repository must contain a safe owner and repository path.");
        }

        // Give the worker one canonical form so its independent shell validation
        // agrees with the server-side URI parser (including uppercase/default-port input).
        var canonicalHost = uri.IdnHost switch
        {
            "www.github.com" => "github.com",
            "www.gitlab.com" => "gitlab.com",
            _ => uri.IdnHost
        };
        return $"https://{canonicalHost}{path}";
    }

    public static void ValidateBuildInputs(string? gitRef, string? pluginDirectory, string? buildConfig)
    {
        if (!string.IsNullOrEmpty(gitRef) &&
            (gitRef.Length > MaxGitRefCharacters || gitRef.Any(char.IsControl)))
            throw new BuildServiceException("Git ref is too long or contains control characters.");

        if (!string.IsNullOrEmpty(pluginDirectory))
        {
            var segments = pluginDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (pluginDirectory.Length > MaxPluginDirectoryCharacters ||
                pluginDirectory.StartsWith('/') ||
                pluginDirectory.Any(char.IsControl) ||
                segments.Any(segment => segment is "." or ".."))
                throw new BuildServiceException("Plugin directory is not a safe relative path.");
        }

        if (!string.IsNullOrEmpty(buildConfig) &&
            !Regex.IsMatch(buildConfig, "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant))
            throw new BuildServiceException("Build configuration is invalid.");
    }

    public static IReadOnlyList<string> CreateInternalNetworkArguments(string network, string label) =>
    [
        "network", "create", "--driver", "bridge", "--internal", "--ipv6=false",
        "--opt", "com.docker.network.bridge.inhibit_ipv4=true", "--label", label, network
    ];

    public static IReadOnlyList<string> CreateProxyArguments(
        string container, string network, string resolverFile, string image, string label) =>
    [
        "container", "create",
        "--name", container,
        "--label", label,
        "--runtime", "runsc",
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
        string sourceVolume,
        string workVolume,
        string outputVolume,
        string workerImageId,
        FullBuildId buildId,
        BuildInfo buildInfo,
        TimeSpan timeout)
    {
        var proxy = $"http://{proxyIp}:3128";
        var timeoutSeconds = Math.Max(1L, (long)Math.Ceiling(timeout.TotalSeconds));
        List<string> arguments =
        [
            "container", "create",
            "--name", containerName,
            "--label", $"{BuildExecutorDocker.ManagedResourceLabel}={buildId}",
            "--network", internalNetwork,
            "--dns", "192.0.2.1",
            "--dns-option", "timeout:1",
            "--dns-option", "attempts:1",
            "--runtime", "runsc",
            "--read-only",
            "--user", $"{WorkerUserId}:{WorkerUserId}",
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges:true",
            "--memory", "3g",
            "--memory-swap", "3g",
            "--cpus", "2",
            "--pids-limit", "512",
            "--ulimit", "nofile=4096:4096",
            "--stop-timeout", "5",
            "--log-driver", "none",
            "--tmpfs", $"/tmp:rw,exec,nosuid,nodev,size=512m,mode=0700,uid={WorkerUserId},gid={WorkerUserId}",
            "--mount", $"type=bind,source={sourceVolume},target=/source,readonly",
            "--mount", $"type=bind,source={workVolume},target=/build",
            "--mount", $"type=bind,source={outputVolume},target=/out",
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
            "--env", "GIT_CONFIG_NOSYSTEM=1",
            "--env", $"BUILD_TIMEOUT_SECONDS={timeoutSeconds.ToString(CultureInfo.InvariantCulture)}",
            "--env", $"BUILD_LOG_MAX_BYTES={MaxBuildLogBytes.ToString(CultureInfo.InvariantCulture)}",
            "--env", $"BUILD_LOG_MAX_LINE_BYTES={MaxBuildLogLineBytes.ToString(CultureInfo.InvariantCulture)}",
            "--env", $"BUILD_LOG_MAX_LINES={MaxBuildLogLines.ToString(CultureInfo.InvariantCulture)}"
        ];

        if (!string.IsNullOrEmpty(buildInfo.PluginDir))
            arguments.AddRange(["--env", $"PLUGIN_DIR={buildInfo.PluginDir}"]);
        if (!string.IsNullOrEmpty(buildInfo.BuildConfig))
            arguments.AddRange(["--env", $"BUILD_CONFIG={buildInfo.BuildConfig}"]);

        arguments.Add(workerImageId);
        return arguments;
    }

    public sealed class PreparedBuild : IAsyncDisposable
    {
        private readonly DockerBuildSandbox _owner;
        private readonly FullBuildId _buildId;
        private readonly BuildInfo _buildInfo;
        private readonly string _workerImageId;
        private readonly string _proxyImageId;
        private readonly CancellationToken _stopToken;
        private readonly string _resourceSuffix = Guid.NewGuid().ToString("N");
        private readonly List<(DockerResourceKind Kind, string Name)> _resources = [];
        private readonly HashSet<(DockerResourceKind Kind, string Name)> _ambiguousCreates = [];
        private bool _disposed;
        private bool _proxyResolverFileRemoved;
        private readonly int _scratchSlot;
        private bool _scratchSlotReleased;

        internal PreparedBuild(
            DockerBuildSandbox owner,
            FullBuildId buildId,
            BuildInfo buildInfo,
            string workerImageId,
            string proxyImageId)
        {
            _owner = owner;
            _buildId = buildId;
            _buildInfo = buildInfo;
            _workerImageId = workerImageId;
            _proxyImageId = proxyImageId;
            _stopToken = owner._executorState.StopToken;
            WorkerContainer = $"pb-worker-{_resourceSuffix}";
            CloneContainer = $"pb-clone-{_resourceSuffix}";
            ProxyContainer = $"pb-proxy-{_resourceSuffix}";
            InternalNetwork = $"pb-internal-{_resourceSuffix}";
            EgressNetwork = $"pb-egress-{_resourceSuffix}";
            var scratchRoot = owner._options.BuildScratchRoot
                ?? throw new BuildServiceException("The isolated build scratch directory is not configured.");
            _scratchSlot = owner.AcquireScratchSlot();
            ScratchDirectory = Path.Combine(ScratchSlotPath(scratchRoot, _scratchSlot), $"pb-build-{_resourceSuffix}");
            SourceVolume = Path.Combine(ScratchDirectory, "source");
            WorkVolume = Path.Combine(ScratchDirectory, "work");
            OutputVolume = Path.Combine(ScratchDirectory, "output");
            StagingVolume = Path.Combine(ScratchDirectory, "staging");
            ProxyResolverFile = Path.Combine(WorkVolume, ProxyResolverFileName);
        }

        public string WorkerContainer { get; }
        public string CloneContainer { get; }
        public string ProxyContainer { get; }
        public string InternalNetwork { get; }
        public string EgressNetwork { get; }
        public string ScratchDirectory { get; }
        public string SourceVolume { get; }
        public string WorkVolume { get; }
        public string OutputVolume { get; }
        public string StagingVolume { get; }
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
                CreateProxyArguments(ProxyContainer, EgressNetwork, ProxyResolverFile, _proxyImageId, Label));
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
                    SourceVolume,
                    WorkVolume,
                    OutputVolume,
                    _workerImageId,
                    _buildId,
                    _buildInfo,
                    _owner._options.BuildTimeout));
        }

        public async Task<StagedBuildOutput> RunAndStageAsync(
            IOutputCapture buildOutput,
            DataReceivedEventHandler? onOutput,
            DataReceivedEventHandler? onError)
        {
            ThrowIfDisposed();
            var current = _owner._executorState.Snapshot;
            if (!current.IsReady || current.WorkerImageId != _workerImageId || current.ProxyImageId != _proxyImageId)
                throw new BuildServiceException("The isolated build executor became unavailable.");

            int code;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopToken))
            {
                timeout.CancelAfter(_owner._options.BuildTimeout + TimeSpan.FromSeconds(15));
                try
                {
                    code = await _owner._processRunner.RunAsync(new ProcessSpec
                    {
                        // Bound the raw attach stream outside the worker's PID namespace.
                        // This still catches output written directly to /proc/1/fd/1.
                        Executable = "/usr/bin/perl",
                        Arguments =
                        [
                            BuildExecutorDocker.OutputLimiterPath,
                            MaxBuildLogBytes.ToString(CultureInfo.InvariantCulture),
                            MaxBuildLogLineBytes.ToString(CultureInfo.InvariantCulture),
                            MaxBuildLogLines.ToString(CultureInfo.InvariantCulture),
                            Math.Max(1L, (long)Math.Ceiling(_owner._options.BuildTimeout.TotalSeconds))
                                .ToString(CultureInfo.InvariantCulture),
                            "--",
                            "docker", "container", "start", "--attach", WorkerContainer
                        ],
                        OutputCapture = buildOutput,
                        ErrorCapture = buildOutput,
                        OnOutput = onOutput,
                        OnError = onError
                    }, timeout.Token);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    await RemoveWorkerOrThrow();
                    if (_stopToken.IsCancellationRequested)
                        throw new BuildServiceException("The isolated build executor was stopped.");
                    throw new BuildServiceException($"Plugin build timed out after {_owner._options.BuildTimeout}.");
                }
                catch
                {
                    await RemoveWorkerOrThrow();
                    throw;
                }
            }

            await RemoveWorkerOrThrow();

            if (code == 124)
                throw new BuildServiceException($"Plugin build timed out after {_owner._options.BuildTimeout}.");
            if (code == 78)
                throw new BuildServiceException("Plugin build output exceeded its configured limit.");
            if (code != 0)
                throw new BuildServiceException("Plugin build failed.");

            await StageArtifacts();
            var buildEnvironmentJson = await ReadStagedFile("build-env.json", MaxBuildMetadataBytes);
            var manifestJson = await ReadStagedFile("manifest.json", MaxBuildMetadataBytes);
            var trustedHash = (await ReadStagedFile("artifact.sha256", 256)).Trim();

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
            if (!Regex.IsMatch(trustedHash, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant))
                throw new BuildServiceException("The staged plugin artifact has an invalid SHA-256 digest.");

            // These fields describe the server-side request, not attacker-controlled output.
            buildEnvironment["gitRepository"] = _buildInfo.GitRepository;
            buildEnvironment["gitRef"] = _buildInfo.GitRef;
            buildEnvironment["pluginDir"] = _buildInfo.PluginDir;
            buildEnvironment["buildConfig"] = _buildInfo.BuildConfig ?? "Release";
            buildEnvironment["buildHash"] = trustedHash;

            await CleanupUntrustedResources();
            return new StagedBuildOutput(buildEnvironment, manifestJson, assemblyName, StagingVolume);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;
            await CleanupResources(throwOnFailure: true);
        }

        internal async Task DisposeAfterFailureAsync()
        {
            if (_disposed)
                return;
            _disposed = true;
            await CleanupResources(throwOnFailure: false);
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
                "--runtime", "runsc",
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
                "--mount", $"type=bind,source={SourceVolume},target=/source",
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
            timeout.CancelAfter(CloneTimeout + TimeSpan.FromSeconds(15));
            try
            {
                code = await _owner._processRunner.RunAsync(new ProcessSpec
                {
                    Executable = "/usr/bin/perl",
                    Arguments =
                    [
                        BuildExecutorDocker.OutputLimiterPath,
                        MaxCloneLogBytes.ToString(CultureInfo.InvariantCulture),
                        MaxBuildLogLineBytes.ToString(CultureInfo.InvariantCulture),
                        MaxCloneLogLines.ToString(CultureInfo.InvariantCulture),
                        ((long)CloneTimeout.TotalSeconds).ToString(CultureInfo.InvariantCulture),
                        "--",
                        "docker", "container", "start", "--attach", CloneContainer
                    ],
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

            if (code == 124)
                throw new BuildServiceException("The repository checkout timed out.");
            if (code == 78)
                throw new BuildServiceException("The repository checkout output exceeded its configured limit.");
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
                    "--runtime", "runsc",
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
                    "--mount", $"type=bind,source={OutputVolume},target=/untrusted-output,readonly",
                    "--mount", $"type=bind,source={SourceVolume},target=/source,readonly",
                    "--mount", $"type=bind,source={StagingVolume},target=/staging",
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
                var directory = new DirectoryInfo(StagingVolume);
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

        private async Task CleanupUntrustedResources()
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
            var expectedPrefix = configuredRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullScratchPath = Path.GetFullPath(ScratchDirectory);
            if (!fullScratchPath.StartsWith(expectedPrefix, StringComparison.Ordinal))
                throw new BuildServiceException("The isolated build scratch path is invalid.");

            // Track before the first write so partial creation is still reconciled.
            _resources.Add((DockerResourceKind.Directory, ScratchDirectory));
            Directory.CreateDirectory(SourceVolume);
            Directory.CreateDirectory(WorkVolume);
            Directory.CreateDirectory(OutputVolume);
            Directory.CreateDirectory(StagingVolume);
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
                    "--runtime", "runsc",
                    "--network", "none",
                    "--read-only",
                    "--user", "0:0",
                    "--cap-drop", "ALL",
                    "--cap-add", "CHOWN",
                    "--security-opt", "no-new-privileges:true",
                    "--memory", "64m",
                    "--memory-swap", "64m",
                    "--pids-limit", "16",
                    "--ulimit", "nofile=64:64",
                    "--log-driver", "none",
                    "--mount", $"type=bind,source={ScratchDirectory},target=/scratch",
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
            {
                // Hold the slot through staging, upload and cleanup, not just compilation.
                if (!_scratchSlotReleased)
                {
                    _owner.ReleaseScratchSlot(_scratchSlot);
                    _scratchSlotReleased = true;
                }
                return;
            }

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

    public sealed record StagedBuildOutput(
        JObject BuildEnvironment,
        string ManifestJson,
        string AssemblyName,
        string StagingVolume);

    private enum DockerResourceKind
    {
        Container,
        Network,
        Directory
    }
}
