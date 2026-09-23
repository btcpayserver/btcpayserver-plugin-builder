using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using PluginBuilder.BuildBroker;
using PluginBuilder.Configuration;
using PluginBuilder.HostedServices;
using PluginBuilder.Services;
using PluginBuilder.Util;
using PluginBuilder.Util.Extensions;
using Xunit;

using PluginBuilder.Builds.BuildBroker;
using PluginBuilder.Builds.Services;

namespace PluginBuilder.Tests;

public class RemoteBuildSandboxTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string Lease = "11111111111111111111111111111111";
    private const string Instance = "22222222222222222222222222222222";
    private static readonly string Image = "sha256:" + new string('a', 64);
    private static readonly byte[] Artifact = Encoding.UTF8.GetBytes("canonical plugin package");
    private static readonly string ArtifactHash = Convert.ToHexStringLower(SHA256.HashData(Artifact));

    // The token format itself is covered by BuildBrokerSecurityTests.InvalidSecretFilesFailClosedAtStartup,
    // through the same BuildBrokerProtocol.TryReadTokenFile; here only the client's wiring matters.
    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    public async Task MissingOrInvalidSecretFailsClosedBeforeNetworkAccess(string? token)
    {
        using var fixture = new Fixture(token);
        await fixture.Monitor.CheckOnceAsync();
        Assert.False(fixture.State.Snapshot.IsReady);
        Assert.Empty(fixture.Transport.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StableInstancePreservesBuildsButConfirmedRestartCancelsThem(bool replacementReady)
    {
        using var fixture = new Fixture();
        fixture.Transport.Enqueue(Json(Ready()));
        await fixture.Monitor.CheckOnceAsync();
        var firstToken = fixture.State.StopToken;
        fixture.Transport.Enqueue(Json(Ready() with { WorkerImageId = "sha256:" + new string('b', 64) }));
        await fixture.Monitor.CheckOnceAsync();
        Assert.False(firstToken.IsCancellationRequested);
        Assert.Equal(firstToken, fixture.State.StopToken);
        fixture.Transport.EnqueueAccepted();
        await using var accepted = await fixture.Client.PrepareAsync(new("example-plugin", 7), Build());
        TaskCompletionSource polling = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Transport.Enqueue(async (_, token) =>
        {
            polling.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Restart should cancel polling.");
        });
        var running = accepted.RunAndStageAsync(new OutputCapture());
        await polling.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Transport.Enqueue(Json(Ready() with { InstanceId = new string('3', 32), IsReady = replacementReady }));
        fixture.Transport.InstanceId = new string('3', 32);
        await fixture.Monitor.CheckOnceAsync();
        Assert.True(firstToken.IsCancellationRequested);
        Assert.Equal(replacementReady, fixture.State.Snapshot.IsReady);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        fixture.Transport.Enqueue(Json(Ready() with { InstanceId = new string('3', 32) }));
        await fixture.Monitor.CheckOnceAsync();
        Assert.True(fixture.State.Snapshot.IsReady);
        Assert.False(fixture.State.StopToken.IsCancellationRequested);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("connection")]
    [InlineData("unavailable")]
    [InlineData("unauthorized")]
    [InlineData("invalid")]
    [InlineData("not-ready")]
    public async Task FailedHealthPollsSuspendAdmissionButAcceptedBuildsSurviveRecovery(string failure)
    {
        using var fixture = new Fixture();
        fixture.Transport.Enqueue(Json(Ready()));
        await fixture.Monitor.CheckOnceAsync();
        var first = fixture.State.StopToken;
        fixture.Transport.EnqueueAccepted();
        await using var accepted = await fixture.Client.PrepareAsync(new("example-plugin", 7), Build());
        // There is no failure-count threshold that abandons accepted leases.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            fixture.Transport.Enqueue((_, _) => failure switch
            {
                "timeout" => throw new TaskCanceledException("The status deadline expired."),
                "connection" => throw new HttpRequestException("The connection was reset."),
                "unavailable" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
                "unauthorized" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)),
                "invalid" => Task.FromResult(Json("invalid status")),
                "not-ready" => Task.FromResult(Json(Ready() with { IsReady = false })),
                _ => throw new InvalidOperationException()
            });
            await fixture.Monitor.CheckOnceAsync();
            Assert.False(fixture.State.Snapshot.IsReady);
            Assert.False(first.IsCancellationRequested);
            Assert.Equal(first, fixture.State.StopToken);
            var requests = fixture.Transport.Requests.Count;
            await Assert.ThrowsAsync<BuildServiceException>(() => fixture.Client.PrepareAsync(new("example-plugin", 8), Build()));
            Assert.Equal(requests, fixture.Transport.Requests.Count);
        }

        // The accepted lease can finish even before admission recovers.
        fixture.Transport.Enqueue(Json(new BrokerBuildStatus("succeeded", 0, [], Result(), null)));
        fixture.Transport.Enqueue(Bytes(Artifact));
        var staged = await accepted.RunAndStageAsync(new OutputCapture());
        Assert.Equal(Artifact, await File.ReadAllBytesAsync(Path.Combine(staged.StagingDirectory, "artifact.btcpay")));
        fixture.Transport.Enqueue(Json(Ready()));
        await fixture.Monitor.CheckOnceAsync();
        Assert.True(fixture.State.Snapshot.IsReady);
        Assert.Equal(first, fixture.State.StopToken);
        Assert.False(first.IsCancellationRequested);
    }

    [Fact]
    public async Task TruncatedHealthResponseSuspendsAdmissionWithoutCancellingAcceptedGeneration()
    {
        var truncate = false;
        await using var server = await LoopbackServer.Start(async context =>
        {
            context.Response.Headers[RemoteBuildSandbox.InstanceHeader] = Instance;
            context.Response.ContentType = "application/json";
            var json = JsonSerializer.Serialize(Ready(), JsonOptions);
            context.Response.ContentLength = Encoding.UTF8.GetByteCount(json);
            await context.Response.WriteAsync(truncate ? json[..5] : json);
            await context.Response.Body.FlushAsync();
            // Returning with fewer bytes than Content-Length closes the response early.
        });
        using var fixture = new Fixture(brokerUrl: server.Address);
        await fixture.Monitor.CheckOnceAsync();
        Assert.True(fixture.State.Snapshot.IsReady);
        var first = fixture.State.StopToken;
        truncate = true;
        await fixture.Monitor.CheckOnceAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(fixture.State.Snapshot.IsReady);
        Assert.False(first.IsCancellationRequested);
        truncate = false;
        await fixture.Monitor.CheckOnceAsync();
        Assert.True(fixture.State.Snapshot.IsReady);
        Assert.Equal(first, fixture.State.StopToken);
    }

    [Theory]
    [InlineData("invalid", "valid")]
    [InlineData("valid", "docker-tag")]
    public async Task InvalidReadyIdentityIsNeverTrusted(string instance, string image)
    {
        using var fixture = new Fixture();
        fixture.Transport.Enqueue(Json(Ready() with
        {
            InstanceId = instance == "valid" ? Instance : instance,
            WorkerImageId = image == "valid" ? Image : image
        }));
        await fixture.Monitor.CheckOnceAsync();
        Assert.False(fixture.State.Snapshot.IsReady);
    }

    [Fact]
    public async Task SubmissionSendsOnlyBuildInputsAndNeverRetriesAnAmbiguousPost()
    {
        using var fixture = new Fixture();
        fixture.Transport.Enqueue((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("http://build-broker:8080/v1/builds", request.RequestUri!.AbsoluteUri);
            throw new HttpRequestException("sensitive diagnostic must not be public");
        });
        var error = await Assert.ThrowsAnyAsync<BuildServiceException>(() => fixture.Client.PrepareAsync(new("example-plugin", 7), Build()));
        Assert.DoesNotContain("sensitive", error.Message);
        Assert.Single(fixture.Transport.Requests);
        using var body = JsonDocument.Parse(fixture.Transport.Requests[0].Body!);
        Assert.Equal(new[] { "pluginSlug", "buildId", "gitRepository", "gitRef", "pluginDir", "buildConfig" }.Order(StringComparer.Ordinal),
            body.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("../other")]
    [InlineData("https://attacker.invalid")]
    [InlineData("1111111111111111111111111111111Z")]
    public async Task InvalidLeaseCannotSelectADifferentEndpoint(string lease)
    {
        using var fixture = new Fixture();
        fixture.Transport.Enqueue(Json(new BrokerBuildAccepted(lease), HttpStatusCode.Accepted));
        await Assert.ThrowsAsync<BuildServiceException>(() => fixture.Client.PrepareAsync(new("example-plugin", 7), Build()));
        Assert.Single(fixture.Transport.Requests);
    }

    [Fact]
    public async Task SubmissionReturnsImmediatelyWithoutStartOrCleanupRequests()
    {
        using var fixture = new Fixture();
        fixture.Transport.EnqueueAccepted();
        await using var submitted = await fixture.Client.PrepareAsync(new("example-plugin", 7), Build());
        Assert.Single(fixture.Transport.Requests);
        Assert.Equal("POST", fixture.Transport.Requests[0].Method);
        await submitted.DisposeAsync();
        Assert.Single(fixture.Transport.Requests);
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOrShutdownStopsLocalPollingWithoutCoordinatingBrokerCleanup(bool shutdown)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        TaskCompletionSource polling = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Transport.EnqueueAccepted();
        fixture.Transport.Enqueue(async (_, token) =>
        {
            polling.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Poll should be cancelled.");
        });
        await using var submitted = await fixture.Client.PrepareAsync(new("example-plugin", 7), Build(), cancellation.Token);
        var running = submitted.RunAndStageAsync(new OutputCapture());
        await polling.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (shutdown) await fixture.Client.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        else cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        await submitted.DisposeAsync();
        Assert.Equal(new[] { "POST", "GET" }, fixture.Transport.Requests.Select(r => r.Method));
    }

    [Fact]
    public async Task SuccessfulBuildDownloadsVerifiedPrivateArtifactWithoutStartOrCleanupRequests()
    {
        using var fixture = new Fixture();
        fixture.Transport.EnqueueAccepted();
        fixture.Transport.Enqueue(Json(new BrokerBuildStatus("preparing", 0, [], null, null)));
        fixture.Transport.Enqueue(Json(new BrokerBuildStatus("running", 1, ["clone done"], null, null)));
        fixture.Transport.Enqueue(Json(new BrokerBuildStatus("succeeded", 2, ["build done"], Result(), null)));
        fixture.Transport.Enqueue(Bytes(Artifact));
        var prepared = await fixture.Client.PrepareAsync(new("example-plugin", 7), Build());
        var output = new OutputCapture();
        var staged = await prepared.RunAndStageAsync(output);
        Assert.Equal(new[] { "clone done", "build done" }, output.Lines);
        Assert.Equal(Artifact, await File.ReadAllBytesAsync(Path.Combine(staged.StagingDirectory, "artifact.btcpay")));
        Assert.Equal(ArtifactHash, staged.BuildEnvironment["buildHash"]!.Value<string>());
        Assert.StartsWith(Path.Combine(fixture.Directory, "broker-staging") + Path.DirectorySeparatorChar, staged.StagingDirectory);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(staged.StagingDirectory));
        await prepared.DisposeAsync();
        await prepared.DisposeAsync();
        Assert.False(System.IO.Directory.Exists(staged.StagingDirectory));
        Assert.Single(fixture.Transport.Requests, request => request.Method == "POST");
        Assert.All(fixture.Transport.Requests.Skip(1), request =>
        {
            Assert.Equal("GET", request.Method);
            Assert.Contains(request.Path.Split('?')[0], new[] { $"/v1/builds/{Lease}", $"/v1/builds/{Lease}/artifact" });
        });
        Assert.Equal($"/v1/builds/{Lease}/artifact", fixture.Transport.Requests[^1].Path);
        Assert.Equal(fixture.Transport.Requests.Count - 1, fixture.Transport.PinnedInstances.Count);
        Assert.All(fixture.Transport.PinnedInstances, instance => Assert.Equal(Instance, instance));
    }

    [Fact]
    public async Task PendingLogPagesAreDrainedWithoutOneSecondDelayPerPage()
    {
        using var fixture = new Fixture();
        fixture.Transport.EnqueueAccepted();
        for (var cursor = 1; cursor <= 20; cursor++)
            fixture.Transport.Enqueue(Json(new BrokerBuildStatus("running", cursor, ["queued log"], null, null)));
        fixture.Transport.Enqueue(Json(new BrokerBuildStatus("succeeded", 20, [], Result(), null)));
        fixture.Transport.Enqueue(Bytes(Artifact));
        await using var prepared = await fixture.Client.PrepareAsync(new("example-plugin", 7), Build());
        var output = new OutputCapture();
        var staged = await prepared.RunAndStageAsync(output).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(20, output.Lines.Count());
        Assert.Equal(Artifact, await File.ReadAllBytesAsync(Path.Combine(staged.StagingDirectory, "artifact.btcpay")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOrShutdownDuringDownloadRemovesLocalStaging(bool shutdown)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        TaskCompletionSource downloading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Transport.EnqueueAccepted();
        fixture.Transport.Enqueue(Json(new BrokerBuildStatus("succeeded", 0, [], Result(), null)));
        fixture.Transport.Enqueue(async (_, token) =>
        {
            downloading.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Download should be cancelled.");
        });
        await using var submitted = await fixture.Client.PrepareAsync(new("example-plugin", 7), Build(), cancellation.Token);
        var running = submitted.RunAndStageAsync(new OutputCapture());
        await downloading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(System.IO.Directory.EnumerateDirectories(Path.Combine(fixture.Directory, "broker-staging")));
        if (shutdown) await fixture.Client.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        else cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        await submitted.DisposeAsync();
        Assert.Empty(System.IO.Directory.EnumerateDirectories(Path.Combine(fixture.Directory, "broker-staging")));
        Assert.Equal(new[] { "POST", "GET", "GET" }, fixture.Transport.Requests.Select(r => r.Method));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrDifferentInstanceDownloadCannotReturnArtifact(bool brokerRestarted)
    {
        using var fixture = new Fixture();
        fixture.Transport.EnqueueAccepted();
        fixture.Transport.Enqueue(Json(new BrokerBuildStatus("succeeded", 0, [], Result(), null)));
        fixture.Transport.Enqueue((_, _) =>
        {
            if (brokerRestarted) fixture.Transport.InstanceId = new string('3', 32);
            return Task.FromResult(brokerRestarted ? Bytes(Artifact) : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });
        await using var submitted = await fixture.Client.PrepareAsync(new("example-plugin", 7), Build());
        await Assert.ThrowsAsync<BuildServiceException>(() => submitted.RunAndStageAsync(new OutputCapture()));
        await submitted.DisposeAsync();
        Assert.Empty(System.IO.Directory.EnumerateDirectories(Path.Combine(fixture.Directory, "broker-staging")));
    }

    [Fact]
    public async Task EscapingHeavyMetadataStillFitsTheBoundedWireEnvelope()
    {
        using var fixture = new Fixture();
        var result = Result();
        var environment = JObject.Parse(result.BuildEnvironmentJson);
        var manifest = JObject.Parse(result.ManifestJson);
        var padding = new string('<', BuildPolicy.MaxBuildMetadataBytes - 2048);
        environment["padding"] = padding;
        manifest["padding"] = padding;
        result = result with
        {
            BuildEnvironmentJson = environment.ToString(Newtonsoft.Json.Formatting.None),
            ManifestJson = manifest.ToString(Newtonsoft.Json.Formatting.None)
        };
        var response = Json(new BrokerBuildStatus("succeeded", 0, [], result, null));
        Assert.True(response.Content.Headers.ContentLength > 10 * 1024 * 1024);
        Assert.True(response.Content.Headers.ContentLength < RemoteBuildSandbox.MaximumStatusBytes);
        fixture.Transport.EnqueueAccepted();
        fixture.Transport.Enqueue(response);
        fixture.Transport.Enqueue(Bytes(Artifact));
        var prepared = await fixture.Client.PrepareAsync(new("example-plugin", 7), Build());
        var staged = await prepared.RunAndStageAsync(new OutputCapture());
        Assert.Equal(padding, staged.BuildEnvironment["padding"]!.Value<string>());
        await prepared.DisposeAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BuildStatusEnvelopeOverSixteenMiBIsRejected(bool withContentLength)
    {
        using var fixture = new Fixture();
        var json = new string(' ', RemoteBuildSandbox.MaximumStatusBytes + 1) +
                   JsonSerializer.Serialize(new BrokerBuildStatus("succeeded", 0, [], Result(), null), JsonOptions);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = withContentLength
                ? new StringContent(json, Encoding.UTF8, "application/json")
                : new UnknownLengthJsonContent(json)
        };
        Assert.Equal(withContentLength, response.Content.Headers.ContentLength.HasValue);
        fixture.Transport.EnqueueAccepted();
        fixture.Transport.Enqueue(response);
        // Without the envelope limit, this valid result must be able to finish;
        // an unrelated missing artifact response must not satisfy ThrowsAsync.
        fixture.Transport.Enqueue((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal($"/v1/builds/{Lease}/artifact", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Bytes(Artifact));
        });
        await using var prepared = await fixture.Client.PrepareAsync(new("example-plugin", 7), Build());
        await Assert.ThrowsAsync<BuildServiceException>(() => prepared.RunAndStageAsync(new OutputCapture()));
        Assert.DoesNotContain(fixture.Transport.Requests, request => request.Path.EndsWith("/artifact", StringComparison.Ordinal));
        await prepared.DisposeAsync();
    }

    [Fact]
    public async Task HostedShutdownDisposesLocalHandlesWithoutDeletingBrokerJobs()
    {
        using var fixture = new Fixture();
        fixture.Transport.EnqueueAccepted();
        fixture.Transport.EnqueueAccepted(new string('4', 32));
        var first = await fixture.Client.PrepareAsync(new("example-plugin", 7), Build());
        var second = await fixture.Client.PrepareAsync(new("example-plugin", 8), Build());
        await fixture.Monitor.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(fixture.State.Snapshot.IsReady);
        await first.DisposeAsync();
        await second.DisposeAsync();
        Assert.Equal(new[] { "POST", "POST" }, fixture.Transport.Requests.Select(r => r.Method));
        await Assert.ThrowsAsync<BuildServiceException>(() => fixture.Client.PrepareAsync(new("example-plugin", 9), Build()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InFlightPostSurvivesAdmissionSuspensionButIsDrainedOnShutdown(bool shutdown)
    {
        using var fixture = new Fixture();
        TaskCompletionSource posting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource respond = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Transport.Enqueue(async (_, token) =>
        {
            posting.TrySetResult();
            await respond.Task.WaitAsync(token);
            return Json(new BrokerBuildAccepted(Lease), HttpStatusCode.Accepted);
        });
        var submission = fixture.Client.PrepareAsync(new("example-plugin", 7), Build());
        await posting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (shutdown)
        {
            var stopping = fixture.Client.StopAsync();
            Assert.Same(stopping, fixture.Client.StopAsync());
            Assert.False(stopping.IsCompleted);
            respond.TrySetResult();
            await Assert.ThrowsAsync<BuildServiceException>(() => submission);
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        }
        else
        {
            fixture.State.SuspendAdmission("Health probe failed during submission.");
            respond.TrySetResult();
            await using var accepted = await submission.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(fixture.State.Snapshot.IsReady);
        }
        Assert.Single(fixture.Transport.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedBuildRequestIsNotRetriedAndDoesNotCancelAnotherLease(bool duringDownload)
    {
        using var fixture = new Fixture();
        fixture.Transport.EnqueueAccepted();
        fixture.Transport.EnqueueAccepted(new string('4', 32));
        await using var first = await fixture.Client.PrepareAsync(new("example-plugin", 7), Build());
        await using var second = await fixture.Client.PrepareAsync(new("example-plugin", 8), Build());
        if (duringDownload)
            fixture.Transport.Enqueue(Json(new BrokerBuildStatus("succeeded", 0, [], Result(), null)));
        fixture.Transport.Enqueue((_, _) => throw new HttpRequestException("Connection lost."));
        await Assert.ThrowsAsync<BuildServiceException>(() => first.RunAndStageAsync(new OutputCapture()));
        Assert.Equal(duringDownload ? 4 : 3, fixture.Transport.Requests.Count);
        Assert.False(fixture.State.StopToken.IsCancellationRequested);

        fixture.Transport.Enqueue(Json(new BrokerBuildStatus("succeeded", 0, [], Result(), null)));
        fixture.Transport.Enqueue(Bytes(Artifact));
        var staged = await second.RunAndStageAsync(new OutputCapture());
        Assert.Equal(Artifact, await File.ReadAllBytesAsync(Path.Combine(staged.StagingDirectory, "artifact.btcpay")));
    }


    [Fact]
    public async Task ConcurrentDisposeAndShutdownDoNotDeadlockOrContactBroker()
    {
        using var fixture = new Fixture();
        fixture.Transport.EnqueueAccepted();
        var submitted = await fixture.Client.PrepareAsync(new("example-plugin", 7), Build());
        await Task.WhenAll(submitted.DisposeAsync().AsTask(), fixture.Client.StopAsync())
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(fixture.Transport.Requests);
    }

    [Fact]
    public async Task LateReadinessResponseCannotMakeAStoppedClientReadyAgain()
    {
        using var fixture = new Fixture();
        TaskCompletionSource polling = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource respond = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Transport.Enqueue(async (_, _) =>
        {
            polling.TrySetResult();
            await respond.Task;
            return Json(Ready());
        });
        var checking = fixture.Monitor.CheckOnceAsync();
        await polling.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Monitor.StopAsync(CancellationToken.None);
        respond.TrySetResult();
        await checking;
        Assert.False(fixture.State.Snapshot.IsReady);
    }

    [Theory]
    [InlineData("assemblyName", "../../etc/passwd")]
    [InlineData("buildHash", "untrusted")]
    [InlineData("gitRepository", "https://github.com/attacker/repo")]
    [InlineData("gitRef", "other")]
    [InlineData("pluginDir", "other")]
    [InlineData("buildConfig", "Debug")]
    [InlineData("gitCommit", "not-a-commit")]
    [InlineData("gitCommitDate", "not-a-date")]
    [InlineData("buildDate", "not-a-date")]
    public async Task MismatchedOrInvalidMetadataRejectsBeforeDownloadingArtifact(string property, string value)
    {
        using var fixture = new Fixture();
        var result = Result();
        var env = JsonSerializer.Deserialize<Dictionary<string, object?>>(result.BuildEnvironmentJson)!;
        env[property] = value;
        result = result with { BuildEnvironmentJson = JsonSerializer.Serialize(env) };
        await AssertInvalidResult(fixture, result);
        Assert.DoesNotContain(fixture.Transport.Requests, request => request.Path.EndsWith("/artifact"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(268435457)]
    public async Task EmptyOrOversizedArtifactMetadataIsRejected(long length)
    {
        using var fixture = new Fixture();
        await AssertInvalidResult(fixture, Result() with { ArtifactLength = length });
    }

    [Theory]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("hash")]
    [InlineData("length-header")]
    [InlineData("encoded")]
    public async Task TruncatedOversizedOrChangedArtifactIsRejectedAndRemoved(string failure)
    {
        using var fixture = new Fixture();
        fixture.Transport.EnqueueAccepted();
        fixture.Transport.Enqueue(Json(new BrokerBuildStatus("succeeded", 0, [], Result(), null)));
        var bytes = failure switch
        {
            "short" => Artifact[..^1],
            "long" => Artifact.Concat(new byte[] { 1 }).ToArray(),
            "hash" => new byte[Artifact.Length],
            _ => Artifact
        };
        var response = Bytes(bytes);
        response.Content.Headers.ContentLength = failure == "length-header" ? Artifact.Length + 1 : Artifact.Length;
        if (failure == "encoded")
            response.Content.Headers.ContentEncoding.Add("gzip");
        fixture.Transport.Enqueue(response);
        var prepared = await fixture.Client.PrepareAsync(new("example-plugin", 7), Build());
        await Assert.ThrowsAsync<BuildServiceException>(() => prepared.RunAndStageAsync(new OutputCapture()));
        await prepared.DisposeAsync();
        Assert.Empty(System.IO.Directory.EnumerateDirectories(Path.Combine(fixture.Directory, "broker-staging")));
    }

    [Theory]
    [InlineData("cursor")]
    [InlineData("line")]
    [InlineData("newline")]
    [InlineData("unknown-state")]
    public async Task InvalidProgressResponseFailsClosed(string failure)
    {
        using var fixture = new Fixture();
        var status = failure switch
        {
            "cursor" => new BrokerBuildStatus("running", 3, ["one"], null, null),
            "line" => new BrokerBuildStatus("running", 1, [new string('a', 64 * 1024)], null, null),
            "newline" => new BrokerBuildStatus("running", 1, ["one\ntwo"], null, null),
            _ => new BrokerBuildStatus("unknown", 0, [], null, null)
        };
        fixture.Transport.EnqueueAccepted();
        fixture.Transport.Enqueue(Json(status));
        var prepared = await fixture.Client.PrepareAsync(new("example-plugin", 7), Build());
        await Assert.ThrowsAsync<BuildServiceException>(() => prepared.RunAndStageAsync(new OutputCapture()));
        await prepared.DisposeAsync();
    }

    [Fact]
    public async Task BrokerFailureNeverDownloadsOrPublishesAnArtifact()
    {
        using var fixture = new Fixture();
        fixture.Transport.EnqueueAccepted();
        fixture.Transport.Enqueue(Json(new BrokerBuildStatus("failed", 0, [], null, "Plugin build failed in the isolated executor.")));
        await using var submitted = await fixture.Client.PrepareAsync(new("example-plugin", 7), Build());
        var error = await Assert.ThrowsAsync<BuildServiceException>(() => submitted.RunAndStageAsync(new OutputCapture()));
        Assert.Equal("Plugin build failed in the isolated executor.", error.Message);
        Assert.Equal(new[] { "POST", "GET" }, fixture.Transport.Requests.Select(r => r.Method));
    }


    [Fact]
    public async Task CancellationDuringPostLeavesAcceptedJobToBrokerDeadline()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Transport.Enqueue((_, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(Json(new BrokerBuildAccepted(Lease), HttpStatusCode.Accepted));
        });
        await Assert.ThrowsAsync<BuildServiceException>(() => fixture.Client.PrepareAsync(new("example-plugin", 7), Build(), cancellation.Token));
        Assert.Single(fixture.Transport.Requests);
    }


    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task ProductionTransportRejectsRedirectWithoutContactingItsTarget(HttpStatusCode redirectStatus)
    {
        var targetRequests = new ConcurrentQueue<string>();
        await using var target = await LoopbackServer.Start(context =>
        {
            targetRequests.Enqueue(context.Request.Method);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });
        var originRequests = new ConcurrentQueue<(string Method, string Path, string Authorization)>();
        await using var origin = await LoopbackServer.Start(context =>
        {
            originRequests.Enqueue((context.Request.Method, context.Request.Path.ToString(), context.Request.Headers.Authorization.ToString()));
            context.Response.StatusCode = (int)redirectStatus;
            context.Response.Headers.Location = new Uri(target.Address, "redirect-target").AbsoluteUri;
            return Task.CompletedTask;
        });
        // This selects the production constructor and its real SocketsHttpHandler,
        // rather than a fake transport that never follows redirects by itself.
        using var fixture = new Fixture(brokerUrl: origin.Address);
        await Assert.ThrowsAsync<BuildServiceException>(() => fixture.Client.PrepareAsync(new("example-plugin", 7), Build())
            .WaitAsync(TimeSpan.FromSeconds(5)));

        var received = Assert.Single(originRequests);
        Assert.Equal("POST", received.Method);
        Assert.Equal("/v1/builds", received.Path);
        Assert.Equal("Bearer " + new string('a', 64), received.Authorization);
        Assert.Empty(targetRequests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedStatusIsRejectedBeforeDeserialization(bool withContentLength)
    {
        using var fixture = new Fixture();
        var json = new string(' ', 16385) + JsonSerializer.Serialize(Ready(), JsonOptions);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = withContentLength
                ? new StringContent(json, Encoding.UTF8, "application/json")
                : new UnknownLengthJsonContent(json)
        };
        Assert.Equal(withContentLength, response.Content.Headers.ContentLength.HasValue);
        fixture.Transport.Enqueue(response);
        await fixture.Monitor.CheckOnceAsync();
        Assert.False(fixture.State.Snapshot.IsReady);
    }

    private static async Task AssertInvalidResult(Fixture fixture, BrokerBuildResult result)
    {
        fixture.Transport.EnqueueAccepted();
        fixture.Transport.Enqueue(Json(new BrokerBuildStatus("succeeded", 0, [], result, null)));
        var prepared = await fixture.Client.PrepareAsync(new("example-plugin", 7), Build());
        await Assert.ThrowsAsync<BuildServiceException>(() => prepared.RunAndStageAsync(new OutputCapture()));
        await prepared.DisposeAsync();
    }

    private static BuildInfo Build() => new()
    {
        GitRepository = "https://github.com/example/plugin", GitRef = "main", PluginDir = "Plugin", BuildConfig = null!
    };

    private static BrokerStatus Ready() => new(true, Image, Image, null, Instance);

    private static BrokerBuildResult Result() => new(JsonSerializer.Serialize(new
    {
        assemblyName = "Example.Plugin", buildHash = ArtifactHash,
        gitRepository = Build().GitRepository, gitRef = Build().GitRef, pluginDir = Build().PluginDir,
        buildConfig = "Release", gitCommit = new string('b', 40),
        gitCommitDate = "2026-09-10T12:34:56Z", buildDate = "2026-09-10T12:35:56Z"
    }), "{\"Identifier\":\"Example.Plugin\",\"Version\":\"1.0.0\"}", "Example.Plugin", Artifact.Length, ArtifactHash);

    private static HttpResponseMessage Json(object value, HttpStatusCode code = HttpStatusCode.OK) => new(code)
    {
        Content = new StringContent(JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Bytes(byte[] value) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(value)
    };

    private sealed class UnknownLengthJsonContent(string json) : StringContent(json, Encoding.UTF8, "application/json")
    {
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(TestPaths.GetPhysicalDirectoryPath(Path.GetTempPath()), "pb-broker-client-tests-" + Guid.NewGuid().ToString("N"));
        public Transport Transport { get; } = new();
        public BuildExecutorState State { get; } = new();
        public RemoteBuildSandbox Client { get; }
        public BuildBrokerMonitor Monitor { get; }

        public Fixture(string? token = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Uri? brokerUrl = null)
        {
            System.IO.Directory.CreateDirectory(Directory);
            var path = Path.Combine(Directory, "token");
            if (token is not null)
                File.WriteAllText(path, token);
            var options = new PluginBuilderOptions
            {
                DataDir = Directory, BuildBrokerTokenFile = token is null ? null : path,
                BuildBrokerUrl = brokerUrl ?? new Uri("http://build-broker:8080/")
            };
            Client = brokerUrl is null
                ? new RemoteBuildSandbox(options, State, NullLogger<RemoteBuildSandbox>.Instance, Transport)
                : new RemoteBuildSandbox(options, State, NullLogger<RemoteBuildSandbox>.Instance);
            State.MarkReady(Image, Image);
            Monitor = new BuildBrokerMonitor(Client, State, NullLogger<BuildBrokerMonitor>.Instance);
        }

        public void Dispose()
        {
            Client.Dispose();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    private sealed class LoopbackServer(WebApplication application) : IAsyncDisposable
    {
        public Uri Address { get; private set; } = null!;

        public static async Task<LoopbackServer> Start(RequestDelegate handler)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            app.Run(handler);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await app.StartAsync(timeout.Token);
                return new LoopbackServer(app)
                {
                    Address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
                        .Addresses.Single())
                };
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await application.StopAsync(timeout.Token);
            }
            finally
            {
                await application.DisposeAsync();
            }
        }
    }

    private sealed class Transport : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _responses = new();
        public List<(string Method, string Host, string Path, string? Body)> Requests { get; } = [];
        public string InstanceId { get; set; } = Instance;
        public List<string> PinnedInstances { get; } = [];
        public void EnqueueAccepted(string lease = Lease)
        {
            Enqueue(Json(new BrokerBuildAccepted(lease), HttpStatusCode.Accepted));
        }


        public void Enqueue(HttpResponseMessage response) => Enqueue((_, _) => Task.FromResult(response));
        public void Enqueue(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response)
        {
            lock (_gate) _responses.Enqueue(response);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal(new AuthenticationHeaderValue("Bearer", new string('a', 64)), request.Headers.Authorization);
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(token);
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> next;
            lock (_gate)
            {
                Requests.Add((request.Method.Method, request.RequestUri!.Host, request.RequestUri.PathAndQuery, body));
                if (request.Headers.TryGetValues(RemoteBuildSandbox.InstanceHeader, out var instances))
                    PinnedInstances.Add(Assert.Single(instances));
                Assert.NotEmpty(_responses);
                next = _responses.Dequeue();
            }
            var response = await next(request, token);
            response.Headers.Add(RemoteBuildSandbox.InstanceHeader, InstanceId);
            return response;
        }
    }
}
