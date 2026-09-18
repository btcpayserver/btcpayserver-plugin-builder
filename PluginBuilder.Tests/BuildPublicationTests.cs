using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json.Linq;
using PluginBuilder.Events;
using PluginBuilder.HostedServices;
using PluginBuilder.Services;
using PluginBuilder.Util;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

using PluginBuilder.Builds;
using PluginBuilder.Builds.Services;

namespace PluginBuilder.Tests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class BuildPublicationTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    private static readonly byte[] Artifact = BuildBrokerSecurityTests.FakePrepared.ArtifactBytes;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RequiresRemoteCleanupBeforeAnyUploadAndRemovesLocalStaging(bool failCleanup, bool failUpload)
    {
        await using var storage = await AzureStagedUploadContractTests.BlobServer.Start(rejectRequest: failUpload);
        await using var broker = await BuildBrokerSecurityTests.BrokerFixture.Start();
        var stagingRoot = Path.Combine(broker.Root, "web-data", "broker-staging");
        using var cleanup = new CleanupObserver(stagingRoot);
        TaskCompletionSource allowCleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        broker.Sandbox.CompleteImmediately = true;
        broker.Sandbox.FailDisposal = failCleanup;
        broker.Sandbox.DisposalBlockedUntil = allowCleanup.Task;
        broker.Sandbox.ManifestJson = """{"Identifier":"Test.Plugin","Name":"Test Plugin","Version":"1.0.0"}""";
        await using var tester = Create("BuildPublication");
        tester.ReuseDatabase = false;
        tester.ConfigureServices = services =>
        {
            // Keep the real HTTP broker, remote client, BuildService and Azure SDK.
            // Only sandbox execution and the loopback storage endpoint are fakes.
            foreach (var service in services.Where(service => service.ServiceType == typeof(IHostedService) &&
                         (service.ImplementationType == typeof(BuildBrokerMonitor) ||
                          service.ImplementationType == typeof(AzureStartupHostedService))).ToArray())
                services.Remove(service);
            services.RemoveAll<RemoteBuildSandbox>();
            services.AddSingleton(provider => broker.CreateRemoteSandbox(
                provider.GetRequiredService<BuildExecutorState>(), cleanup));
            services.RemoveAll<IBuildSandbox>();
            services.AddSingleton<IBuildSandbox>(provider => provider.GetRequiredService<RemoteBuildSandbox>());
            services.RemoveAll<AzureStorageClient>();
            services.AddSingleton(storage.CreateClient());
            services.RemoveAll<IGitHostingProvider>();
        };
        await tester.Start();
        var executor = tester.GetService<BuildExecutorState>();
        executor.MarkReady("sha256:" + new string('1', 64), "sha256:" + new string('2', 64));
        var user = await tester.CreateFakeUserAsync();
        var slug = new PluginSlug("publication-" + Guid.NewGuid().ToString("N")[..8]);
        await using var connection = await tester.GetService<DBConnectionFactory>().Open();
        Assert.True(await connection.NewPlugin(slug, user));
        var id = new FullBuildId(slug, await connection.NewBuild(slug,
            new PluginBuildParameters("https://github.com/example/plugin")));

        var events = new ConcurrentQueue<(string State, bool CleanupAcknowledged, bool ArtifactExists)>();
        using var subscription = tester.GetService<EventAggregator>().Subscribe<BuildChanged>(evt =>
        {
            if (evt.FullBuildId == id)
                events.Enqueue((evt.EventName, cleanup.Acknowledged, File.Exists(cleanup.StagedArtifact)));
        });

        var execution = tester.GetService<BuildService>().Build(id);
        Exception? error;
        try
        {
            await cleanup.Requested.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // Sending DELETE is insufficient: no upload may start until the
            // issuing broker acknowledges that its remote resources are gone.
            Assert.False(execution.IsCompleted);
            Assert.False(cleanup.Acknowledged);
            Assert.Empty(storage.Requests);
            Assert.Null(storage.StoredArtifact);
            Assert.DoesNotContain(events, evt => evt.State == BuildStates.WaitingUpload.ToEventName() ||
                evt.State == BuildStates.Uploading.ToEventName() || evt.State == BuildStates.Uploaded.ToEventName());
            Assert.Equal(Artifact, await File.ReadAllBytesAsync(cleanup.StagedArtifact!));
        }
        finally
        {
            allowCleanup.TrySetResult();
            error = await Record.ExceptionAsync(() => execution.WaitAsync(TimeSpan.FromSeconds(10)));
        }

        Assert.Equal(1, cleanup.RequestCount);
        Assert.False(File.Exists(cleanup.StagedArtifact));
        Assert.Empty(Directory.EnumerateFileSystemEntries(stagingRoot));
        Assert.Equal(!failCleanup, broker.Sandbox.Prepared[id.BuildId].IsDisposed);

        var state = await connection.QuerySingleAsync<string>(
            "SELECT state FROM builds WHERE plugin_slug=@slug AND id=@buildId",
            new { slug = slug.ToString(), buildId = id.BuildId });
        var versions = (await connection.QueryAsync<long>(
            "SELECT build_id FROM versions WHERE plugin_slug=@slug", new { slug = slug.ToString() })).ToArray();
        if (failCleanup)
        {
            Assert.IsType<BuildServiceException>(error);
            Assert.Empty(storage.Requests);
            Assert.Null(storage.StoredArtifact);
            Assert.False(cleanup.Acknowledged);
            Assert.DoesNotContain(events, evt => evt.State == BuildStates.Uploading.ToEventName());
        }
        else
        {
            Assert.True(cleanup.Acknowledged);
            var uploading = Assert.Single(events, evt => evt.State == BuildStates.Uploading.ToEventName());
            Assert.True(uploading.CleanupAcknowledged);
            Assert.True(uploading.ArtifactExists);
            var upload = Assert.Single(storage.Requests);
            Assert.Equal($"/satoshi/artifacts/{id}/Test.Plugin.btcpay", upload.Path);
            Assert.Equal("*", upload.IfNoneMatch);
            Assert.Equal(Artifact, upload.Body);
            if (failUpload)
            {
                Assert.IsType<AzureStorageClientException>(error);
                Assert.Null(storage.StoredArtifact);
            }
            else
            {
                Assert.Null(error);
                Assert.Equal(Artifact, storage.StoredArtifact);
            }
        }

        if (failCleanup || failUpload)
        {
            Assert.Equal(BuildStates.Failed.ToEventName(), state);
            Assert.Empty(versions);
            Assert.DoesNotContain(events, evt => evt.State == BuildStates.Uploaded.ToEventName());
            Assert.Contains(events, evt => evt.State == BuildStates.Failed.ToEventName());
        }
        else
        {
            Assert.Equal(BuildStates.Uploaded.ToEventName(), state);
            Assert.Equal(id.BuildId, Assert.Single(versions));
            var published = Assert.Single(events, evt => evt.State == BuildStates.Uploaded.ToEventName());
            Assert.True(published.CleanupAcknowledged);
            Assert.False(published.ArtifactExists);
        }
    }

    [Fact]
    public async Task ExecutionSlotRemainsHeldThroughUploadAndLocalDisposal()
    {
        var uploads = Channel.CreateUnbounded<AzureStagedUploadContractTests.BlobRequest>();
        var releaseUploads = new ConcurrentDictionary<string, TaskCompletionSource>();
        TaskCompletionSource releaseAllUploads = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var storage = await AzureStagedUploadContractTests.BlobServer.Start(beforeResponse: async (request, token) =>
        {
            var release = releaseUploads.GetOrAdd(request.Path,
                _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            uploads.Writer.TryWrite(request);
            await Task.WhenAny(release.Task, releaseAllUploads.Task).WaitAsync(token);
        });
        using var sandbox = new UploadSandbox();
        await using var tester = Create("UploadAdmission");
        tester.ReuseDatabase = false;
        tester.ConfigureServices = services =>
        {
            foreach (var service in services.Where(service => service.ServiceType == typeof(IHostedService) &&
                         service.ImplementationType?.Assembly == typeof(DatabaseStartupHostedService).Assembly &&
                         service.ImplementationType != typeof(DatabaseStartupHostedService)).ToArray())
                services.Remove(service);
            services.RemoveAll<IBuildSandbox>();
            services.AddSingleton<IBuildSandbox>(sandbox);
            services.RemoveAll<AzureStorageClient>();
            services.AddSingleton(storage.CreateClient());
            services.RemoveAll<IGitHostingProvider>();
        };
        await tester.Start();
        var executor = tester.GetService<BuildExecutorState>();
        executor.MarkReady("sha256:" + new string('1', 64), "sha256:" + new string('2', 64));
        var user = await tester.CreateFakeUserAsync();
        var slug = new PluginSlug("upload-queue-" + Guid.NewGuid().ToString("N")[..8]);
        await using var connection = await tester.GetService<DBConnectionFactory>().Open();
        Assert.True(await connection.NewPlugin(slug, user));
        List<FullBuildId> ids = [];
        for (var index = 0; index < 3; index++)
            ids.Add(new FullBuildId(slug, await connection.NewBuild(slug,
                new PluginBuildParameters("https://github.com/example/plugin"))));

        var buildService = tester.GetService<BuildService>();
        List<Task> executions = [];
        try
        {
            executions.Add(buildService.Build(ids[0]));
            var firstUpload = await uploads.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(ids[0], await sandbox.NextPrepared());
            executions.Add(buildService.Build(ids[1]));
            var secondUpload = await uploads.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(ids[1], await sandbox.NextPrepared());
            Assert.Equal($"/satoshi/artifacts/{ids[0]}/Example.btcpay", firstUpload.Path);
            Assert.Equal($"/satoshi/artifacts/{ids[1]}/Example.btcpay", secondUpload.Path);

            executions.Add(buildService.Build(ids[2]));
            var thirdPreparing = sandbox.NextPrepared();
            // Remote cleanup has already finished, but both private artifacts
            // still occupy the web application's buffer while Azure is blocked.
            await Assert.ThrowsAsync<TimeoutException>(() => thirdPreparing.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Equal(2, sandbox.Prepared.Count);
            Assert.Equal(2, Directory.GetFiles(sandbox.Root, "artifact.btcpay", SearchOption.AllDirectories).Length);
            Assert.All(executions, execution => Assert.False(execution.IsCompleted));

            releaseUploads[firstUpload.Path].TrySetResult();
            var first = sandbox.Prepared[ids[0]];
            await first.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // Finishing the upload is insufficient: retain the slot until its
            // local staging file has actually been discarded as well.
            await Assert.ThrowsAsync<TimeoutException>(() => thirdPreparing.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.False(executions[0].IsCompleted);
            Assert.True(File.Exists(first.ArtifactPath));
            Assert.Equal(2, sandbox.Prepared.Count);

            first.AllowDisposal.TrySetResult();
            Assert.Equal(ids[2], await thirdPreparing.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(File.Exists(first.ArtifactPath));
            var thirdUpload = await uploads.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal($"/satoshi/artifacts/{ids[2]}/Example.btcpay", thirdUpload.Path);
            Assert.Equal(2, Directory.GetFiles(sandbox.Root, "artifact.btcpay", SearchOption.AllDirectories).Length);

            releaseAllUploads.TrySetResult();
            sandbox.AllowAllDisposals.TrySetResult();
            await Task.WhenAll(executions).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Empty(Directory.EnumerateFileSystemEntries(sandbox.Root));
            Assert.Equal(3, storage.Requests.Count);
            Assert.Equal(3, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM builds WHERE plugin_slug=@slug AND state='uploaded'",
                new { slug = slug.ToString() }));
        }
        finally
        {
            releaseAllUploads.TrySetResult();
            sandbox.AllowAllDisposals.TrySetResult();
            executor.MarkUnavailable("Publication test teardown.");
            await Task.WhenAll(executions.Select(execution =>
                Record.ExceptionAsync(() => execution.WaitAsync(TimeSpan.FromSeconds(10)))));
        }
    }

    // BuildService owns the local-buffer admission bound. Return an upload-ready
    // result directly here; the existing HTTP publication tests cover the broker.
    private sealed class UploadSandbox : IBuildSandbox, IDisposable
    {
        private readonly Channel<FullBuildId> _preparing = Channel.CreateUnbounded<FullBuildId>();
        public string Root { get; } = Directory.CreateTempSubdirectory("pb-upload-admission-").FullName;
        public ConcurrentDictionary<FullBuildId, PreparedBuild> Prepared { get; } = new();
        public TaskCompletionSource AllowAllDisposals { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IPreparedBuild> PrepareAsync(FullBuildId id, BuildInfo info, CancellationToken cancellationToken = default)
        {
            var prepared = new PreparedBuild(this, Path.Combine(Root, id.BuildId.ToString()));
            Assert.True(Prepared.TryAdd(id, prepared));
            _preparing.Writer.TryWrite(id);
            return Task.FromResult<IPreparedBuild>(prepared);
        }

        public Task<FullBuildId> NextPrepared() => _preparing.Reader.ReadAsync().AsTask();
        public void Dispose() => Directory.Delete(Root, recursive: true);

        public sealed class PreparedBuild(UploadSandbox owner, string staging) : IPreparedBuild
        {
            private Task? _disposal;
            public string ArtifactPath => Path.Combine(staging, "artifact.btcpay");
            public TaskCompletionSource Disposing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource AllowDisposal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public async Task<StagedBuildOutput> RunAndStageAsync(IOutputCapture output)
            {
                Directory.CreateDirectory(staging);
                await File.WriteAllBytesAsync(ArtifactPath, Artifact);
                return new StagedBuildOutput(new JObject(),
                    """{"Identifier":"Example","Name":"Example","Version":"1.0.0"}""", "Example", staging);
            }

            public ValueTask DisposeAsync() => new(_disposal ??= DisposeCore());

            private async Task DisposeCore()
            {
                Disposing.TrySetResult();
                await Task.WhenAny(AllowDisposal.Task, owner.AllowAllDisposals.Task);
                if (Directory.Exists(staging))
                    Directory.Delete(staging, recursive: true);
            }
        }
    }

    // Observe the actual HTTP acknowledgement; do not emulate the broker protocol
    // or confuse starting sandbox disposal with confirmation received by the client.
    private sealed class CleanupObserver(string stagingRoot) : DelegatingHandler(new SocketsHttpHandler
        { UseProxy = false, UseCookies = false, AllowAutoRedirect = false })
    {
        public string? StagedArtifact { get; private set; }
        public int RequestCount { get; private set; }
        public bool Acknowledged { get; private set; }
        public TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var isCleanup = request.Method == HttpMethod.Delete;
            if (isCleanup)
            {
                RequestCount++;
                StagedArtifact = Assert.Single(Directory.EnumerateFiles(
                    stagingRoot, "artifact.btcpay", SearchOption.AllDirectories));
                Requested.TrySetResult();
            }
            var response = await base.SendAsync(request, token);
            if (isCleanup)
                Acknowledged = response.StatusCode == HttpStatusCode.NoContent;
            return response;
        }
    }
}
