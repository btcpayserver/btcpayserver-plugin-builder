using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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
    private bool _shutdownCleanupFailed;

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
        if (!IsHex(status.InstanceId, 32) || status.InstanceId != ResponseInstance(response) ||
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
        // A caller cancellation cannot hide a received lease ID from our cleanup.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await SendAsync(HttpMethod.Post, "v1/builds", request, timeout.Token);
        RequireStatus(response, HttpStatusCode.Accepted);
        var instance = ResponseInstance(response);
        var accepted = await ReadJsonAsync<BrokerBuildAccepted>(response, 4096, timeout.Token);
        if (!IsHex(accepted.LeaseId, 32))
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
            // Return only after remote preparation. BuildService must recheck the
            // administrative flag before RunAndStageAsync authorizes execution.
            await prepared.WaitUntilPreparedAsync();
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
            // Stop all existing leases concurrently. In-flight POSTs retain their
            // 30-second response deadline so a received ID can still be deleted;
            // their submission tasks do not finish until that cleanup completes.
            await Task.WhenAll(submissions.Concat(active.Select(lease => lease.DisposeAsync().AsTask())))
                .WaitAsync(_shutdownBudget.Token);
            lock (_lifecycleGate)
                if (_shutdownCleanupFailed)
                    throw new BuildServiceException("The isolated build broker shutdown cleanup could not be confirmed.");
        }
        catch
        {
            _logger.LogError("Build broker shutdown cleanup could not be confirmed before its deadline; broker lease expiry remains the fallback.");
            throw;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body,
        CancellationToken cancellationToken, string? expectedInstance = null)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await ReadTokenAsync(cancellationToken));
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
                        throw new BuildServiceException("The isolated build broker restarted before build cleanup was confirmed.");
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

    private async Task<string> ReadTokenAsync(CancellationToken cancellationToken)
    {
        if (_options.BuildBrokerTokenFile is not { } path || !Path.IsPathFullyQualified(path))
            throw new BuildServiceException("The isolated build broker secret file is not configured.");
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null ||
            (info.Attributes & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0 ||
            info.Length is < 64 or > 66)
            throw new BuildServiceException("The isolated build broker secret file is invalid.");
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = await ReadBoundedAsync(input, 66, cancellationToken);
        var token = Encoding.ASCII.GetString(bytes).TrimEnd('\r', '\n');
        if (!IsHex(token, 64))
            throw new BuildServiceException("The isolated build broker secret file is invalid.");
        return token;
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

    private static bool IsHex(string? value, int length) =>
        value is not null && value.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool IsImageId(string? value) => value is not null && value.StartsWith("sha256:", StringComparison.Ordinal) && IsHex(value[7..], 64);
    private static string ResponseInstance(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues(InstanceHeader, out var values))
            throw ProtocolError();
        var entries = values.ToArray();
        if (entries.Length != 1 || !IsHex(entries[0], 32))
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
        private Task? _remoteCleanupTask;
        private string? _staging;
        private Task? _prepareTask;
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

        public Task WaitUntilPreparedAsync()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
                return _prepareTask ??= WaitUntilPreparedCoreAsync();
            }
        }

        private async Task WaitUntilPreparedCoreAsync()
        {
            var token = _lifetime.Token;
            while (true)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                using var response = await _owner.SendAsync(HttpMethod.Get, $"v1/builds/{_lease}?cursor=0", null, timeout.Token, _instance);
                RequireStatus(response, HttpStatusCode.OK);
                var status = await ReadJsonAsync<BrokerBuildStatus>(response, 16 * 1024, timeout.Token);
                if (status.NextCursor != 0 || status.Logs is not { Length: 0 } || status.Result is not null)
                    throw ProtocolError();
                if (status.State == "failed")
                    throw BuildFailure(status.Error);
                if (status.Error is not null)
                    throw ProtocolError();
                if (status.State == "prepared")
                    return;
                if (status.State != "preparing")
                    throw ProtocolError();
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
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
            // An explicit, instance-bound authorization is the only operation
            // that starts the worker. Never retry an ambiguous start response.
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                using var response = await _owner.SendAsync(HttpMethod.Post, $"v1/builds/{_lease}/start", null, timeout.Token, _instance);
                RequireStatus(response, HttpStatusCode.NoContent);
            }
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
                    case "running" when status.Result is null && status.Error is null:
                        await Task.Delay(TimeSpan.FromSeconds(1), token);
                        break;
                    case "failed" when status.Result is null:
                        // The broker returns its public build error, never its Docker logs.
                        throw BuildFailure(status.Error);
                    case "succeeded" when status.Result is not null && status.Error is null:
                        var staged = await DownloadResultAsync(status.Result, token);
                        // The application owns a verified private copy now. Confirm
                        // remote cleanup before returning anything eligible for upload;
                        // DisposeAsync still owns removal of the local copy.
                        await ConfirmRemoteCleanupAsync();
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
                result.AssemblyName is null || !Regex.IsMatch(result.AssemblyName, "\\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\\z", RegexOptions.CultureInvariant) ||
                result.ArtifactLength is <= 0 or > MaximumArtifactBytes || !IsHex(result.ArtifactSha256, 64))
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
                    !(IsHex(environment["gitCommit"]?.Value<string>(), 40) || IsHex(environment["gitCommit"]?.Value<string>(), 64)) ||
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

        private Task ConfirmRemoteCleanupAsync()
        {
            lock (_gate)
                return _remoteCleanupTask ??= ConfirmRemoteCleanupCoreAsync();
        }

        private async Task ConfirmRemoteCleanupCoreAsync()
        {
            // Use one cached attempt for both successful completion and disposal.
            // Its independent deadline also survives caller/executor cancellation.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_owner._shutdownBudget.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            using var response = await _owner.SendAsync(HttpMethod.Delete, $"v1/builds/{_lease}", null, timeout.Token, _instance);
            RequireStatus(response, HttpStatusCode.NoContent);
        }

        private async Task DisposeTrackedAsync()
        {
            try { await DisposeCoreAsync(); }
            catch
            {
                // A lease accepted while shutdown waits for its POST is not in the
                // initial snapshot. Its cleanup failure must still fail shutdown.
                lock (_owner._lifecycleGate)
                    if (_owner._stopping) _owner._shutdownCleanupFailed = true;
                throw;
            }
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
                if (_prepareTask is { } preparing)
                {
                    try { await preparing.WaitAsync(_owner._shutdownBudget.Token); }
                    catch (Exception) { /* The prepare caller observes its own failure. */ }
                }
                if (_runTask is { } running)
                {
                    try { await running.WaitAsync(_owner._shutdownBudget.Token); }
                    catch (Exception) { /* The run caller observes its own failure. */ }
                }
                // Successful runs already confirmed remote cleanup before upload.
                // Failed/cancelled runs still require it; never retry a failed attempt
                // or mistake another broker instance for a cleanup acknowledgement.
                await ConfirmRemoteCleanupAsync();
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
