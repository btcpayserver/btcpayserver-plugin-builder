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
using Microsoft.Extensions.Configuration;
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

    [Theory]
    [InlineData("unix:///var/run/docker.sock")]
    [InlineData("file:///tmp/broker")]
    [InlineData("http://user:password@build-broker:8080")]
    [InlineData("http://build-broker:8080/other")]
    [InlineData("http://build-broker:8080/?token=secret")]
    [InlineData("http://build-broker:8080/#fragment")]
    [InlineData("build-broker:8080")]
    [InlineData("http://build-broker:8080/\n")]
    public void EndpointIsAFixedOriginWithoutCredentialsOrPaths(string value)
    {
        Assert.Equal("BUILD_BROKER_URL", Assert.Throws<ConfigurationException>(
            () => PluginBuilderOptions.ParseBuildBrokerUrl(value)).Key);
    }

    [Theory]
    [InlineData("http://build-broker:8080", "http://build-broker:8080/")]
    [InlineData("https://build-broker", "https://build-broker/")]
    public void TrustedOperatorCanConfigureHttpOrHttpsOrigin(string value, string expected)
    {
        Assert.Equal(expected, PluginBuilderOptions.ParseBuildBrokerUrl(value).AbsoluteUri);
    }

    [Fact]
    public void ConfigurationDefaultsToBrokerAndDoesNotInventASecret()
    {
        var options = PluginBuilderOptions.ConfigureDataDirAndDebugLog(new ConfigurationBuilder().Build(), null!);
        Assert.Equal("http://build-broker:8080/", options.BuildBrokerUrl.AbsoluteUri);
        Assert.Null(options.BuildBrokerTokenFile);
    }

    [Theory]
    [InlineData("token")]
    [InlineData("./token")]
    [InlineData("/token\nvalue")]
    public void RelativeOrControlCharacterSecretPathIsRejected(string value)
    {
        var conf = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BUILD_BROKER_TOKEN_FILE"] = value
        }).Build();
        Assert.Equal("BUILD_BROKER_TOKEN_FILE", Assert.Throws<ConfigurationException>(
            () => PluginBuilderOptions.ConfigureDataDirAndDebugLog(conf, null!)).Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaz")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaextra")]
    public async Task MissingOrInvalidSecretFailsClosedBeforeNetworkAccess(string? token)
    {
        using var fixture = new Fixture(token);
        await fixture.Monitor.CheckOnceAsync();
        Assert.False(fixture.State.Snapshot.IsReady);
        Assert.Empty(fixture.Transport.Requests);
    }

    [Fact]
    public async Task StableReadinessDoesNotCancelActiveBuildsButRestartDoes()
    {
        using var fixture = new Fixture();
        fixture.Transport.Enqueue(Json(Ready()));
        await fixture.Monitor.CheckOnceAsync();
        var firstToken = fixture.State.StopToken;
        fixture.Transport.Enqueue(Json(Ready()));
        await fixture.Monitor.CheckOnceAsync();
        Assert.False(firstToken.IsCancellationRequested);
        Assert.Equal(firstToken, fixture.State.StopToken);
        fixture.Transport.Enqueue(Json(Ready() with { InstanceId = new string('3', 32) }));
        fixture.Transport.InstanceId = new string('3', 32);
        await fixture.Monitor.CheckOnceAsync();
        Assert.True(firstToken.IsCancellationRequested);
        Assert.True(fixture.State.Snapshot.IsReady);
        Assert.False(fixture.State.StopToken.IsCancellationRequested);
    }

    [Fact]
    public async Task UnavailableBrokerCancelsCurrentExecutorAndRecoveryCreatesANewToken()
    {
        using var fixture = new Fixture();
        fixture.Transport.Enqueue(Json(Ready()));
        await fixture.Monitor.CheckOnceAsync();
        var first = fixture.State.StopToken;
        fixture.Transport.Enqueue(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await fixture.Monitor.CheckOnceAsync();
        Assert.False(fixture.State.Snapshot.IsReady);
        Assert.True(first.IsCancellationRequested);
        var requestsBeforeSubmission = fixture.Transport.Requests.Count;
        await Assert.ThrowsAsync<BuildServiceException>(() => fixture.Client.PrepareAsync(new("example-plugin", 7), Build()));
        Assert.Equal(requestsBeforeSubmission, fixture.Transport.Requests.Count);
        fixture.Transport.Enqueue(Json(Ready()));
        await fixture.Monitor.CheckOnceAsync();
        Assert.True(fixture.State.Snapshot.IsReady);
        Assert.NotEqual(first, fixture.State.StopToken);
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
        var error = await Assert.ThrowsAsync<BuildServiceException>(() => fixture.Client.PrepareAsync(new("example-plugin", 7), Build()));
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
    public async Task PreparationWaitsForPreparedStatusWithoutStartingWorker()
    {
        using var fixture = new Fixture();
        TaskCompletionSource polling = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource respond = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Transport.Enqueue(Json(new BrokerBuildAccepted(Lease), HttpStatusCode.Accepted));
        fixture.Transport.Enqueue(Json(new BrokerBuildStatus("preparing", 0, [], null, null)));
        fixture.Transport.Enqueue(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal($"/v1/builds/{Lease}?cursor=0", request.RequestUri!.PathAndQuery);
            polling.TrySetResult();
            await respond.Task.WaitAsync(token);
            return Json(new BrokerBuildStatus("prepared", 0, [], null, null));
        });
        fixture.Transport.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));
        var preparing = fixture.Client.PrepareAsync(new("example-plugin", 7), Build());
        try
        {
            await polling.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(preparing.IsCompleted);
            Assert.Equal(new[] { "POST", "GET", "GET" }, fixture.Transport.Requests.Select(request => request.Method));
        }
        finally
        {
            respond.TrySetResult();
            await using var prepared = await preparing.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(new[] { "POST", "GET", "GET", "DELETE" }, fixture.Transport.Requests.Select(request => request.Method));
        Assert.Equal(new[] { Instance, Instance, Instance }, fixture.Transport.PinnedInstances);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOrShutdownDuringPreparationConfirmsCleanupBeforeReturning(bool shutdown)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        TaskCompletionSource polling = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource deleting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource acknowledge = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Transport.Enqueue(Json(new BrokerBuildAccepted(Lease), HttpStatusCode.Accepted));
        fixture.Transport.Enqueue(async (_, token) =>
        {
            polling.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("The preparation poll should have been cancelled.");
        });
        fixture.Transport.Enqueue(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.False(token.IsCancellationRequested);
            deleting.TrySetResult();
            await acknowledge.Task.WaitAsync(token);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var preparing = fixture.Client.PrepareAsync(new("example-plugin", 7), Build(), cancellation.Token);
        Task? stopping = null;
        Exception? preparationError;
        Exception? shutdownError = null;
        try
        {
            await polling.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (shutdown)
                stopping = fixture.Client.StopAsync();
            else
                cancellation.Cancel();
            await deleting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(preparing.IsCompleted);
            if (stopping is not null)
                Assert.False(stopping.IsCompleted);
        }
        finally
        {
            cancellation.Cancel();
            acknowledge.TrySetResult();
            preparationError = await Record.ExceptionAsync(() => preparing.WaitAsync(TimeSpan.FromSeconds(5)));
            if (stopping is not null)
                shutdownError = await Record.ExceptionAsync(() => stopping.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        Assert.IsAssignableFrom<OperationCanceledException>(preparationError);
        Assert.Null(shutdownError);
        Assert.Equal(new[] { "POST", "GET", "DELETE" }, fixture.Transport.Requests.Select(request => request.Method));
        Assert.Equal(new[] { Instance, Instance }, fixture.Transport.PinnedInstances);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("running")]
    [InlineData("succeeded")]
    public async Task FailedOrPrematureExecutionStatusRejectsPreparationAndCleansLease(string state)
    {
        using var fixture = new Fixture();
        fixture.Transport.Enqueue(Json(new BrokerBuildAccepted(Lease), HttpStatusCode.Accepted));
        fixture.Transport.Enqueue(Json(new BrokerBuildStatus(state, 0, [],
            state == "succeeded" ? Result() : null, state == "failed" ? "Preparation failed." : null)));
        fixture.Transport.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));
        var error = await Assert.ThrowsAsync<BuildServiceException>(() =>
            fixture.Client.PrepareAsync(new("example-plugin", 7), Build()));
        if (state == "failed")
            Assert.Equal("Preparation failed.", error.Message);
        Assert.Equal(new[] { "POST", "GET", "DELETE" }, fixture.Transport.Requests.Select(request => request.Method));
    }

    [Fact]
    public async Task AmbiguousStartIsNotRetriedAndStillCleansTheKnownLease()
    {
        using var fixture = new Fixture();
        fixture.Transport.EnqueuePrepared();
        fixture.Transport.Enqueue((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal($"/v1/builds/{Lease}/start", request.RequestUri!.AbsolutePath);
            Assert.Null(request.Content);
            Assert.Equal(Instance, Assert.Single(request.Headers.GetValues(RemoteBuildSandbox.InstanceHeader)));
            throw new HttpRequestException("sensitive start diagnostic must not be public");
        });
        fixture.Transport.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));
        await using var prepared = await fixture.Client.PrepareAsync(new("example-plugin", 7), Build());
        var error = await Assert.ThrowsAsync<BuildServiceException>(() => prepared.RunAndStageAsync(new OutputCapture()));
        Assert.DoesNotContain("sensitive", error.Message);
        await prepared.DisposeAsync();
        Assert.Equal(new[] { "POST", "GET", "POST", "DELETE" }, fixture.Transport.Requests.Select(request => request.Method));
    }

    [Fact]
    public async Task SuccessfulBuildDownloadsVerifiedPrivateArtifactAndConfirmsCleanup()
    {
        using var fixture = new Fixture();
        fixture.Transport.EnqueuePrepared();
        fixture.Transport.EnqueueStart();
        fixture.Transport.Enqueue(Json(new BrokerBuildStatus("succeeded", 2, ["clone done", "build done"], Result(), null)));
        fixture.Transport.Enqueue(Bytes(Artifact));
        fixture.Transport.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));
        var prepared = await fixture.Client.PrepareAsync(new("example-plugin", 7), Build());
        var output = new OutputCapture();
        var staged = await prepared.RunAndStageAsync(output);
        Assert.Equal(new[] { "POST", "GET", "POST", "GET", "GET", "DELETE" }, fixture.Transport.Requests.Select(item => item.Method));
        Assert.Equal($"/v1/builds/{Lease}/start", fixture.Transport.Requests[2].Path);
        Assert.Null(fixture.Transport.Requests[2].Body);
        Assert.Equal(new[] { "clone done", "build done" }, output.Lines);
        Assert.Equal(Artifact, await File.ReadAllBytesAsync(Path.Combine(staged.StagingDirectory, "artifact.btcpay")));
        Assert.Equal(ArtifactHash, staged.BuildEnvironment["buildHash"]!.Value<string>());
        Assert.StartsWith(Path.Combine(fixture.Directory, "broker-staging") + Path.DirectorySeparatorChar, staged.StagingDirectory);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(staged.StagingDirectory));
        await prepared.DisposeAsync();
        await prepared.DisposeAsync();
        Assert.False(System.IO.Directory.Exists(staged.StagingDirectory));
        Assert.Equal(new[] { "POST", "GET", "POST", "GET", "GET", "DELETE" }, fixture.Transport.Requests.Select(item => item.Method));
        Assert.Equal($"/v1/builds/{Lease}", fixture.Transport.Requests[^1].Path);
        Assert.Equal(new[] { Instance, Instance, Instance, Instance, Instance }, fixture.Transport.PinnedInstances);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrDifferentInstanceCleanupCannotReturnDownloadedArtifact(bool brokerRestarted)
    {
        using var fixture = new Fixture();
        fixture.Transport.EnqueuePrepared();
        fixture.Transport.EnqueueStart();
        fixture.Transport.Enqueue(Json(new BrokerBuildStatus("succeeded", 0, [], Result(), null)));
        fixture.Transport.Enqueue(Bytes(Artifact));
        fixture.Transport.Enqueue((_, _) =>
        {
            if (brokerRestarted)
                fixture.Transport.InstanceId = new string('3', 32);
            return Task.FromResult(new HttpResponseMessage(brokerRestarted
                ? HttpStatusCode.NoContent : HttpStatusCode.ServiceUnavailable));
        });
        var prepared = await fixture.Client.PrepareAsync(new("example-plugin", 7), Build());

        var error = await Assert.ThrowsAsync<BuildServiceException>(() => prepared.RunAndStageAsync(new OutputCapture()));
        if (brokerRestarted)
            Assert.Contains("restarted", error.Message);
        Assert.Equal(new[] { "POST", "GET", "POST", "GET", "GET", "DELETE" }, fixture.Transport.Requests.Select(item => item.Method));
        Assert.Equal(new[] { Instance, Instance, Instance, Instance, Instance }, fixture.Transport.PinnedInstances);

        // The failed finalization is cached: disposal must not retry an ambiguous DELETE,
        // but must still discard the private local copy that cannot be published.
        await Assert.ThrowsAsync<BuildServiceException>(() => prepared.DisposeAsync().AsTask());
        await Assert.ThrowsAsync<BuildServiceException>(() => prepared.DisposeAsync().AsTask());
        Assert.Single(fixture.Transport.Requests, request => request.Method == "DELETE");
        Assert.Empty(System.IO.Directory.EnumerateDirectories(Path.Combine(fixture.Directory, "broker-staging")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOrShutdownWaitsForInFlightCleanupBeforeDiscardingLocalArtifact(bool shutdown)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        TaskCompletionSource deleting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource respond = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken cleanupToken = default;
        fixture.Transport.EnqueuePrepared();
        fixture.Transport.EnqueueStart();
        fixture.Transport.Enqueue(Json(new BrokerBuildStatus("succeeded", 0, [], Result(), null)));
        fixture.Transport.Enqueue(Bytes(Artifact));
        fixture.Transport.Enqueue(async (_, token) =>
        {
            cleanupToken = token;
            deleting.TrySetResult();
            await respond.Task;
            Assert.False(token.IsCancellationRequested);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var prepared = await fixture.Client.PrepareAsync(new("example-plugin", 7), Build(), cancellation.Token);
        var running = prepared.RunAndStageAsync(new OutputCapture());
        await deleting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var staging = Assert.Single(System.IO.Directory.EnumerateDirectories(Path.Combine(fixture.Directory, "broker-staging")));
        var artifactPath = Path.Combine(staging, "artifact.btcpay");
        Task finishing;
        try
        {
            Assert.False(running.IsCompleted);
            Assert.Equal(Artifact, await File.ReadAllBytesAsync(artifactPath));
            if (shutdown)
            {
                var stopping = fixture.Client.StopAsync();
                // Also finalize from the build caller, as BuildService does in its
                // finally block. This synchronously requests lifetime cancellation
                // without depending on scheduling of the hosted shutdown task.
                finishing = Task.WhenAll(stopping, prepared.DisposeAsync().AsTask());
            }
            else
            {
                cancellation.Cancel();
                finishing = prepared.DisposeAsync().AsTask();
            }
            Assert.False(finishing.IsCompleted);
            Assert.False(running.IsCompleted);
            Assert.False(cleanupToken.IsCancellationRequested);
            Assert.Equal(Artifact, await File.ReadAllBytesAsync(artifactPath));
            Assert.Single(fixture.Transport.Requests, request => request.Method == "DELETE");
        }
        finally
        {
            respond.TrySetResult();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        await finishing.WaitAsync(TimeSpan.FromSeconds(5));
        await prepared.DisposeAsync();
        Assert.False(System.IO.Directory.Exists(staging));
        Assert.Equal(new[] { "POST", "GET", "POST", "GET", "GET", "DELETE" }, fixture.Transport.Requests.Select(request => request.Method));
    }

    [Fact]
    public async Task BrokerRestartCannotTurnMissingLeaseIntoFalseCleanupConfirmation()
    {
        using var fixture = new Fixture();
        fixture.Transport.EnqueuePrepared();
        var prepared = await fixture.Client.PrepareAsync(new("example-plugin", 7), Build());
        fixture.Transport.InstanceId = new string('3', 32);
        fixture.Transport.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));
        var error = await Assert.ThrowsAsync<BuildServiceException>(() => prepared.DisposeAsync().AsTask());
        Assert.Contains("restarted", error.Message);
        Assert.Equal(new[] { Instance, Instance }, fixture.Transport.PinnedInstances);
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
        fixture.Transport.EnqueuePrepared();
        fixture.Transport.EnqueueStart();
        fixture.Transport.Enqueue(response);
        fixture.Transport.Enqueue(Bytes(Artifact));
        fixture.Transport.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));
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
        fixture.Transport.EnqueuePrepared();
        fixture.Transport.EnqueueStart();
        fixture.Transport.Enqueue(response);
        // Without the envelope limit, this valid result must be able to finish;
        // an unrelated missing artifact response must not satisfy ThrowsAsync.
        fixture.Transport.Enqueue((request, _) =>
        {
            if (request.Method == HttpMethod.Delete)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal($"/v1/builds/{Lease}/artifact", request.RequestUri!.AbsolutePath);
            fixture.Transport.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));
            return Task.FromResult(Bytes(Artifact));
        });
        await using var prepared = await fixture.Client.PrepareAsync(new("example-plugin", 7), Build());
        await Assert.ThrowsAsync<BuildServiceException>(() => prepared.RunAndStageAsync(new OutputCapture()));
        Assert.DoesNotContain(fixture.Transport.Requests, request => request.Path.EndsWith("/artifact", StringComparison.Ordinal));
        await prepared.DisposeAsync();
    }

    [Fact]
    public async Task HostedShutdownDeletesAllActiveLeasesBeforeDisposingTransport()
    {
        using var fixture = new Fixture();
        fixture.Transport.EnqueuePrepared();
        fixture.Transport.EnqueuePrepared(new string('4', 32));
        var first = await fixture.Client.PrepareAsync(new("example-plugin", 7), Build());
        var second = await fixture.Client.PrepareAsync(new("example-plugin", 8), Build());
        fixture.Transport.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));
        fixture.Transport.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));
        await fixture.Monitor.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(fixture.State.Snapshot.IsReady);
        Assert.Equal(2, fixture.Transport.Requests.Count(request => request.Method == "DELETE"));
        await first.DisposeAsync();
        await second.DisposeAsync();
        Assert.Equal(6, fixture.Transport.Requests.Count);
        await Assert.ThrowsAsync<BuildServiceException>(() => fixture.Client.PrepareAsync(new("example-plugin", 9), Build()));
        Assert.Equal(6, fixture.Transport.Requests.Count);
    }

    [Fact]
    public async Task ShutdownCancelsRunningPollAndDeletesItsLease()
    {
        using var fixture = new Fixture();
        TaskCompletionSource polling = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Transport.EnqueuePrepared();
        fixture.Transport.EnqueueStart();
        fixture.Transport.Enqueue(async (_, token) =>
        {
            polling.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("The poll should have been cancelled.");
        });
        fixture.Transport.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));
        var prepared = await fixture.Client.PrepareAsync(new("example-plugin", 7), Build());
        var running = prepared.RunAndStageAsync(new OutputCapture());
        await polling.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Client.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(new[] { "POST", "GET", "POST", "GET", "DELETE" }, fixture.Transport.Requests.Select(request => request.Method));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownWaitsForInFlightPostAndCleansTheLateAcceptedLease(bool cleanupFails)
    {
        using var fixture = new Fixture();
        TaskCompletionSource posting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource respond = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Transport.Enqueue(async (_, token) =>
        {
            posting.TrySetResult();
            await respond.Task;
            // Keep the bounded accepted response observable so shutdown knows which lease to delete.
            Assert.False(token.IsCancellationRequested);
            return Json(new BrokerBuildAccepted(Lease), HttpStatusCode.Accepted);
        });
        fixture.Transport.Enqueue(new HttpResponseMessage(cleanupFails ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NoContent));
        var preparing = fixture.Client.PrepareAsync(new("example-plugin", 7), Build());
        await posting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = fixture.Client.StopAsync();
        Assert.Same(stopping, fixture.Client.StopAsync());
        Assert.False(stopping.IsCompleted);
        await Assert.ThrowsAsync<BuildServiceException>(() => fixture.Client.PrepareAsync(new("example-plugin", 8), Build()));
        respond.TrySetResult();
        await Assert.ThrowsAsync<BuildServiceException>(() => preparing);
        if (cleanupFails)
            await Assert.ThrowsAsync<BuildServiceException>(() => stopping);
        else
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "POST", "DELETE" }, fixture.Transport.Requests.Select(request => request.Method));
    }

    [Fact]
    public async Task ConcurrentPerBuildDisposeAndShutdownDeleteOnlyOnceWithoutDeadlocking()
    {
        using var fixture = new Fixture();
        TaskCompletionSource deleting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource respond = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Transport.EnqueuePrepared();
        fixture.Transport.Enqueue(async (_, _) =>
        {
            deleting.TrySetResult();
            await respond.Task;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var prepared = await fixture.Client.PrepareAsync(new("example-plugin", 7), Build());
        var disposing = prepared.DisposeAsync().AsTask();
        await deleting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = fixture.Client.StopAsync();
        respond.TrySetResult();
        await Task.WhenAll(disposing, stopping).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "POST", "GET", "DELETE" }, fixture.Transport.Requests.Select(request => request.Method));
    }

    [Fact]
    public async Task ShutdownDeadlineCancelsAnUnresponsiveDelete()
    {
        using var fixture = new Fixture();
        using var deadline = new CancellationTokenSource();
        TaskCompletionSource deleting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Transport.EnqueuePrepared();
        fixture.Transport.Enqueue(async (_, token) =>
        {
            deleting.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("The delete should have been cancelled.");
        });
        var prepared = await fixture.Client.PrepareAsync(new("example-plugin", 7), Build());
        var stopping = fixture.Client.StopAsync(deadline.Token);
        await deleting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        deadline.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopping.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prepared.DisposeAsync().AsTask());
        Assert.False(fixture.State.Snapshot.IsReady);
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
        fixture.Transport.EnqueuePrepared();
        fixture.Transport.EnqueueStart();
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
        fixture.Transport.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));
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
    public async Task InvalidProgressResponseFailsClosedAndCleansLease(string failure)
    {
        using var fixture = new Fixture();
        var status = failure switch
        {
            "cursor" => new BrokerBuildStatus("running", 3, ["one"], null, null),
            "line" => new BrokerBuildStatus("running", 1, [new string('a', 64 * 1024)], null, null),
            "newline" => new BrokerBuildStatus("running", 1, ["one\ntwo"], null, null),
            _ => new BrokerBuildStatus("unknown", 0, [], null, null)
        };
        fixture.Transport.EnqueuePrepared();
        fixture.Transport.EnqueueStart();
        fixture.Transport.Enqueue(Json(status));
        fixture.Transport.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));
        var prepared = await fixture.Client.PrepareAsync(new("example-plugin", 7), Build());
        await Assert.ThrowsAsync<BuildServiceException>(() => prepared.RunAndStageAsync(new OutputCapture()));
        await prepared.DisposeAsync();
        Assert.Equal("DELETE", fixture.Transport.Requests[^1].Method);
    }

    [Fact]
    public async Task CleanupFailurePropagatesInsteadOfAllowingPublication()
    {
        using var fixture = new Fixture();
        fixture.Transport.EnqueuePrepared();
        fixture.Transport.Enqueue(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var prepared = await fixture.Client.PrepareAsync(new("example-plugin", 7), Build());
        await Assert.ThrowsAsync<BuildServiceException>(() => prepared.DisposeAsync().AsTask());
        await Assert.ThrowsAsync<BuildServiceException>(() => prepared.DisposeAsync().AsTask());
        Assert.Equal(3, fixture.Transport.Requests.Count);
    }

    [Fact]
    public async Task CancellationDuringPostStillCleansAnAcceptedLease()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Transport.Enqueue((_, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(Json(new BrokerBuildAccepted(Lease), HttpStatusCode.Accepted));
        });
        fixture.Transport.Enqueue((request, token) =>
        {
            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.False(token.IsCancellationRequested);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        await Assert.ThrowsAsync<BuildServiceException>(() => fixture.Client.PrepareAsync(new("example-plugin", 7), Build(), cancellation.Token));
        Assert.Equal(new[] { "POST", "DELETE" }, fixture.Transport.Requests.Select(item => item.Method));
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
        fixture.Transport.EnqueuePrepared();
        fixture.Transport.EnqueueStart();
        fixture.Transport.Enqueue(Json(new BrokerBuildStatus("succeeded", 0, [], result, null)));
        fixture.Transport.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));
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
        public void EnqueuePrepared(string lease = Lease)
        {
            Enqueue(Json(new BrokerBuildAccepted(lease), HttpStatusCode.Accepted));
            Enqueue(Json(new BrokerBuildStatus("prepared", 0, [], null, null)));
        }

        public void EnqueueStart() => Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));

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
