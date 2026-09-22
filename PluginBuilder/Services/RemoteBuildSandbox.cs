using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Newtonsoft.Json.Linq;
using PluginBuilder.Configuration;
using PluginBuilder.Util;

using PluginBuilder.Builds;
using PluginBuilder.Builds.BuildBroker;
using PluginBuilder.Builds.Services;

namespace PluginBuilder.Services;

/// <summary>
/// The public application speaks only the build protocol. It has no Docker fallback,
/// accepts no caller-selected endpoint, and never shares a writable path with a worker.
/// </summary>
public sealed class RemoteBuildSandbox : IBuildSandbox, IDisposable
{
    public const long MaximumArtifactBytes = BuildBrokerProtocol.MaximumArtifactBytes;
    public const string InstanceHeader = BuildBrokerProtocol.InstanceHeader;
    // Two independent 1 MiB metadata strings can expand sixfold when the
    // surrounding JSON escapes characters. Keep the wire envelope bounded too.
    public const int MaximumStatusBytes = BuildBrokerProtocol.MaximumStatusBytes;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };
    private readonly PluginBuilderOptions _options;
    private readonly BuildExecutorState _executor;
    private readonly ILogger<RemoteBuildSandbox> _logger;
    private readonly HttpClient _http;
    private readonly object _lifecycleGate = new();
    private readonly HashSet<PreparedBuild> _activeLeases = [];
    private readonly HashSet<Task> _submissions = [];
    private readonly CancellationTokenSource _shutdownBudget = new();
    private Task? _stopTask;
    private bool _stopping;

    public RemoteBuildSandbox(PluginBuilderOptions options, BuildExecutorState executor, ILogger<RemoteBuildSandbox> logger)
        : this(options, executor, logger, new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            MaxResponseHeadersLength = 16,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        }) { }

    // The injectable transport lets contract tests exercise malformed and interrupted
    // responses without running Docker or accepting external network connections.
    public RemoteBuildSandbox(PluginBuilderOptions options, BuildExecutorState executor,
        ILogger<RemoteBuildSandbox> logger, HttpMessageHandler transport)
    {
        _options = options;
        _executor = executor;
        _logger = logger;
        _http = new HttpClient(transport, disposeHandler: true)
        {
            BaseAddress = PluginBuilderOptions.ParseBuildBrokerUrl(options.BuildBrokerUrl.AbsoluteUri),
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public async Task<BrokerStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await SendAsync(HttpMethod.Get, "v1/status", null, timeout.Token);
        RequireStatus(response, HttpStatusCode.OK);
        var status = await ReadJsonAsync<BrokerStatus>(response, 16 * 1024, timeout.Token);
        if (!BuildPolicy.IsLowerHex(status.InstanceId, 32) || status.InstanceId != ResponseInstance(response) ||
            (status.IsReady && (!IsImageId(status.WorkerImageId) || !IsImageId(status.ProxyImageId))))
            throw ProtocolError();
        return status;
    }

    public async Task<IPreparedBuild> PrepareAsync(FullBuildId buildId, BuildInfo buildInfo,
        CancellationToken cancellationToken = default)
    {
        TaskCompletionSource submitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lifecycleGate)
        {
            if (_stopping)
                throw new BuildServiceException("The isolated build broker client is stopping.");
            _submissions.Add(submitted.Task);
        }
        try
        {
            return await PrepareCoreAsync(buildId, buildInfo, cancellationToken);
        }
        finally
        {
            lock (_lifecycleGate) _submissions.Remove(submitted.Task);
            submitted.TrySetResult();
        }
    }

    private async Task<IPreparedBuild> PrepareCoreAsync(FullBuildId buildId, BuildInfo buildInfo,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stopToken = _executor.StopToken;
        if (!_executor.Snapshot.IsReady || stopToken.IsCancellationRequested)
            throw new BuildServiceException("The isolated build broker is unavailable.");
        var request = new BrokerBuildRequest(buildId.PluginSlug.ToString(), buildId.BuildId,
            BuildPolicy.NormalizeRepositoryUrl(buildInfo.GitRepository),
            buildInfo.GitRef, buildInfo.PluginDir, string.IsNullOrEmpty(buildInfo.BuildConfig) ? null : buildInfo.BuildConfig);
        BuildPolicy.ValidateBuildInputs(request.GitRef, request.PluginDir, request.BuildConfig);

        // Never retry an ambiguous POST: the broker may already have admitted it.
        // Its hard lease expiry reclaims builds whose accepted response was lost.
        // Orphaned submissions remain owned and bounded by the broker lease.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await SendAsync(HttpMethod.Post, "v1/builds", request, timeout.Token);
        RequireStatus(response, HttpStatusCode.Accepted);
        var instance = ResponseInstance(response);
        var accepted = await ReadJsonAsync<BrokerBuildAccepted>(response, 4096, timeout.Token);
        if (!BuildPolicy.IsLowerHex(accepted.LeaseId, 32))
            throw ProtocolError();
        var prepared = new PreparedBuild(this, accepted.LeaseId, instance, request, stopToken, cancellationToken);
        bool stopping;
        lock (_lifecycleGate)
        {
            // Shutdown waits for every registered submission, including one whose
            // POST response arrives after its initial active-lease snapshot.
            _activeLeases.Add(prepared);
            stopping = _stopping;
        }
        try
        {
            if (stopping || stopToken.IsCancellationRequested || cancellationToken.IsCancellationRequested)
                throw new BuildServiceException("The isolated build executor was stopped.");
            return prepared;
        }
        catch
        {
            await prepared.DisposeAsync();
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycleGate)
        {
            if (_stopTask is not null)
                return _stopTask;
            _stopping = true;
            _shutdownBudget.CancelAfter(TimeSpan.FromMinutes(2));
            // Never invoke lease.DisposeAsync while holding the lifecycle gate:
            // a simultaneous per-build disposal unregisters under that same gate.
            return _stopTask = Task.Run(() => StopCoreAsync(cancellationToken));
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(() => _shutdownBudget.Cancel());
        _executor.MarkUnavailable("The isolated build broker client is stopping.");
        Task[] submissions;
        PreparedBuild[] active;
        lock (_lifecycleGate)
        {
            submissions = _submissions.ToArray();
            active = _activeLeases.ToArray();
        }
        try
        {
            // Stop local polling/downloads and discard local artifacts. Broker jobs
            // finish independently; their deadlines reclaim abandoned results.
            await Task.WhenAll(submissions.Concat(active.Select(lease => lease.DisposeAsync().AsTask())))
                .WaitAsync(_shutdownBudget.Token);
        }
        catch
        {
            _logger.LogError("Build broker client did not stop before its deadline; broker jobs remain independently bounded.");
            throw;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body,
        CancellationToken cancellationToken, string? expectedInstance = null)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ReadToken());
            if (expectedInstance is not null)
                request.Headers.Add(InstanceHeader, expectedInstance);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (body is not null)
                request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (expectedInstance is not null)
            {
                try
                {
                    if (ResponseInstance(response) != expectedInstance)
                        throw new BuildServiceException("The isolated build broker restarted before the result was received.");
                }
                catch { response.Dispose(); throw; }
            }
            return response;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            // Do not publish exception messages, URIs, response text, or token paths.
            _logger.LogWarning("Build broker request failed ({ErrorType})", error.GetType().Name);
            throw new BuildServiceException("The isolated build broker could not be reached or authenticated.");
        }
    }

    private string ReadToken()
    {
        if (_options.BuildBrokerTokenFile is not { } path || !Path.IsPathFullyQualified(path))
            throw new BuildServiceException("The isolated build broker secret file is not configured.");
        return BuildBrokerProtocol.TryReadTokenFile(path, out var token)
            ? token
            : throw new BuildServiceException("The isolated build broker secret file is invalid.");
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, int limit, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > limit ||
            response.Content.Headers.ContentType?.MediaType != "application/json" ||
            response.Content.Headers.ContentEncoding.Count != 0)
            throw ProtocolError();
        await using var input = await response.Content.ReadAsStreamAsync(token);
        var bytes = await ReadBoundedAsync(input, limit, token);
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, JsonOptions) ?? throw ProtocolError();
        }
        catch (JsonException) { throw ProtocolError(); }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream input, int limit, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[Math.Min(limit + 1, 64 * 1024)];
        while (true)
        {
            var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit + 1L - output.Length)), token);
            if (count == 0)
                return output.ToArray();
            output.Write(buffer, 0, count);
            if (output.Length > limit)
                throw ProtocolError();
        }
    }

    private static void RequireStatus(HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode != expected)
            throw new BuildServiceException("The isolated build broker rejected the request or returned an invalid response.");
    }

    private static bool IsImageId(string? value) => value is not null && value.StartsWith("sha256:", StringComparison.Ordinal) && BuildPolicy.IsSha256Hex(value[7..]);
    private static string ResponseInstance(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues(InstanceHeader, out var values))
            throw ProtocolError();
        var entries = values.ToArray();
        if (entries.Length != 1 || !BuildPolicy.IsLowerHex(entries[0], 32))
            throw ProtocolError();
        return entries[0];
    }
    private static BuildServiceException ProtocolError() => new("The isolated build broker returned invalid build data.");
    public void Dispose()
    {
        // Hosted shutdown drains StopAsync before DI disposes the transport.
        _http.Dispose();
        _shutdownBudget.Dispose();
    }

    private sealed class PreparedBuild : IPreparedBuild
    {
        private readonly RemoteBuildSandbox _owner;
        private readonly string _lease;
        private readonly string _instance;
        private readonly BrokerBuildRequest _request;
        private readonly CancellationTokenSource _lifetime;
        private readonly object _gate = new();
        private Task? _disposeTask;
        private string? _staging;
        private Task<StagedBuildOutput>? _runTask;

        public PreparedBuild(RemoteBuildSandbox owner, string lease, string instance, BrokerBuildRequest request,
            CancellationToken stopToken, CancellationToken callerToken)
        {
            _owner = owner;
            _lease = lease;
            _instance = instance;
            _request = request;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(stopToken, callerToken);
            _lifetime.CancelAfter(TimeSpan.FromMinutes(45));
        }

        public Task<StagedBuildOutput> RunAndStageAsync(IOutputCapture output)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
                if (_runTask is not null)
                    throw new InvalidOperationException("This build lease has already been started.");
                return _runTask = RunCoreAsync(output);
            }
        }

        private async Task<StagedBuildOutput> RunCoreAsync(IOutputCapture output)
        {
            var cursor = 0;
            var logBytes = 0;
            var token = _lifetime.Token;
            while (true)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                using var response = await _owner.SendAsync(HttpMethod.Get, $"v1/builds/{_lease}?cursor={cursor}", null, timeout.Token, _instance);
                RequireStatus(response, HttpStatusCode.OK);
                var status = await ReadJsonAsync<BrokerBuildStatus>(response, MaximumStatusBytes, timeout.Token);
                if (status.Logs is null || status.Logs.Length > BuildPolicy.MaxBuildLogLines ||
                    status.NextCursor != cursor + status.Logs.Length || status.NextCursor > BuildPolicy.MaxBuildLogLines)
                    throw ProtocolError();
                var chunkBytes = 0;
                foreach (var line in status.Logs)
                {
                    if (line is null || line.Contains('\n') || line.Contains('\r'))
                        throw ProtocolError();
                    var count = Encoding.UTF8.GetByteCount(line) + 1;
                    if (count > BuildPolicy.MaxBuildLogLineBytes)
                        throw ProtocolError();
                    chunkBytes += count;
                    logBytes += count;
                    if (chunkBytes > 64 * 1024 || logBytes > BuildPolicy.MaxBuildLogBytes)
                        throw ProtocolError();
                    output.AddLine(line);
                }
                cursor = status.NextCursor;
                switch (status.State)
                {
                    case "preparing" when status.Result is null && status.Error is null:
                    case "running" when status.Result is null && status.Error is null:
                        await Task.Delay(TimeSpan.FromSeconds(1), token);
                        break;
                    case "failed" when status.Result is null:
                        // The broker returns its public build error, never its Docker logs.
                        throw BuildFailure(status.Error);
                    case "succeeded" when status.Result is not null && status.Error is null:
                        var staged = await DownloadResultAsync(status.Result, token);
                        // Broker success guarantees sandbox cleanup; disposal owns only our local copy.
                        token.ThrowIfCancellationRequested();
                        return staged;
                    default:
                        throw ProtocolError();
                }
            }
        }

        private static BuildServiceException BuildFailure(string? error) =>
            error is null || Encoding.UTF8.GetByteCount(error) > 4096 || error.Any(char.IsControl)
                ? ProtocolError()
                : new BuildServiceException(error);

        private async Task<StagedBuildOutput> DownloadResultAsync(BrokerBuildResult result, CancellationToken token)
        {
            if (result.BuildEnvironmentJson is null || result.ManifestJson is null ||
                Encoding.UTF8.GetByteCount(result.BuildEnvironmentJson) > BuildPolicy.MaxBuildMetadataBytes ||
                Encoding.UTF8.GetByteCount(result.ManifestJson) > BuildPolicy.MaxBuildMetadataBytes ||
                !BuildPolicy.IsSafeAssemblyName(result.AssemblyName) ||
                result.ArtifactLength is <= 0 or > MaximumArtifactBytes || !BuildPolicy.IsSha256Hex(result.ArtifactSha256))
                throw ProtocolError();
            JObject environment;
            try
            {
                using var parsed = JsonDocument.Parse(result.BuildEnvironmentJson, new JsonDocumentOptions { MaxDepth = 32 });
                using var manifest = JsonDocument.Parse(result.ManifestJson, new JsonDocumentOptions { MaxDepth = 32 });
                if (parsed.RootElement.ValueKind != JsonValueKind.Object || manifest.RootElement.ValueKind != JsonValueKind.Object)
                    throw ProtocolError();
                environment = JObject.Parse(result.BuildEnvironmentJson);
                if (environment["assemblyName"]?.Value<string>() != result.AssemblyName ||
                    environment["buildHash"]?.Value<string>() != result.ArtifactSha256 ||
                    environment["gitRepository"]?.Value<string>() != _request.GitRepository ||
                    environment["gitRef"]?.Value<string>() != _request.GitRef ||
                    environment["pluginDir"]?.Value<string>() != _request.PluginDir ||
                    environment["buildConfig"]?.Value<string>() != (_request.BuildConfig ?? "Release") ||
                    !BuildPolicy.IsGitObjectId(environment["gitCommit"]?.Value<string>()) ||
                    !ValidTimestamp(parsed.RootElement, "gitCommitDate") || !ValidTimestamp(parsed.RootElement, "buildDate"))
                    throw ProtocolError();
            }
            catch (Exception error) when (error is JsonException or Newtonsoft.Json.JsonException or InvalidCastException or FormatException)
            {
                throw ProtocolError();
            }

            token.ThrowIfCancellationRequested();
            var root = Path.Combine(Path.GetFullPath(_owner._options.DataDir), "broker-staging");
            EnsurePrivateDirectory(root);
            var staging = Path.Combine(root, Guid.NewGuid().ToString("N"));
            EnsurePrivateDirectory(staging);
            _staging = staging;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            using var response = await _owner.SendAsync(HttpMethod.Get, $"v1/builds/{_lease}/artifact", null, timeout.Token, _instance);
            RequireStatus(response, HttpStatusCode.OK);
            if (response.Content.Headers.ContentLength != result.ArtifactLength || response.Content.Headers.ContentEncoding.Count != 0)
                throw ProtocolError();
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            var fileOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            };
            if (!OperatingSystem.IsWindows())
                fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using var artifact = new FileStream(Path.Combine(staging, "artifact.btcpay"), fileOptions);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long written = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, result.ArtifactLength - written + 1)), timeout.Token);
                if (read == 0)
                    break;
                written += read;
                if (written > result.ArtifactLength)
                    throw ProtocolError();
                hash.AppendData(buffer, 0, read);
                await artifact.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
            }
            if (written != result.ArtifactLength ||
                !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(result.ArtifactSha256)))
                throw ProtocolError();
            await artifact.FlushAsync(timeout.Token);
            token.ThrowIfCancellationRequested();
            return new StagedBuildOutput(environment, result.ManifestJson, result.AssemblyName, staging);
        }

        private static bool ValidTimestamp(JsonElement environment, string name) =>
            environment.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            value.TryGetDateTimeOffset(out _);

        private static void EnsurePrivateDirectory(string path)
        {
            for (DirectoryInfo? current = new(path); current is not null; current = current.Parent)
            {
                if (current.LinkTarget is not null || (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0))
                    throw ProtocolError();
            }
            if (OperatingSystem.IsWindows())
                Directory.CreateDirectory(path);
            else
            {
                Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (_gate)
                return new ValueTask(_disposeTask ??= DisposeTrackedAsync());
        }

        private async Task DisposeTrackedAsync()
        {
            try { await DisposeCoreAsync(); }
            finally
            {
                lock (_owner._lifecycleGate) _owner._activeLeases.Remove(this);
            }
        }

        private async Task DisposeCoreAsync()
        {
            _lifetime.Cancel();
            try
            {
                // A concurrent cancellation/dispose cannot race a late staging-directory
                // creation or remove an artifact while its download handle is still open.
                if (_runTask is { } running)
                {
                    try { await running.WaitAsync(_owner._shutdownBudget.Token); }
                    catch (Exception) { /* The run caller observes its own failure. */ }
                }
            }
            finally
            {
                _lifetime.Dispose();
                if (_staging is { } directory)
                {
                    // Only our random private directory and one fixed filename are removed.
                    // Never recurse into worker-controlled paths or a shared scratch mount.
                    var info = new DirectoryInfo(directory);
                    if (info.Exists && info.LinkTarget is null && (info.Attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        File.Delete(Path.Combine(directory, "artifact.btcpay"));
                        Directory.Delete(directory, recursive: false);
                    }
                }
            }
        }
    }
}
