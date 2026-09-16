using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using PluginBuilder.BuildBroker;
using PluginBuilder.BuildBroker.Configuration;
using PluginBuilder.Configuration;
using PluginBuilder.HostedServices;
using PluginBuilder.Services;
using PluginBuilder.Util;
using Xunit;

using PluginBuilder.BuildBroker.HostedServices;
using PluginBuilder.Builds;
using PluginBuilder.Builds.BuildBroker;
using PluginBuilder.Builds.Services;

namespace PluginBuilder.Tests;

/// <summary>
/// These tests exercise the actual HTTP boundary with a fake sandbox, not Docker or
/// a real hostile repository. Fixed Docker policy is covered by the sandbox contract tests.
/// </summary>
public class BuildBrokerSecurityTests
{
    private const string InstanceHeader = "X-Build-Broker-Instance";
    private const string WorkerImage = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string ProxyImage = "sha256:2222222222222222222222222222222222222222222222222222222222222222";

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer wrong")]
    [InlineData("Basic YTpi")]
    [InlineData("Bearer aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task EveryRouteRequiresTheBrokerSecretBeforeParsingRequests(string? authorization)
    {
        await using var fixture = await BrokerFixture.Start();
        using var client = fixture.NewClient(authenticated: false);
        if (authorization is not null)
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authorization);

        var opaqueId = new string('b', 32);
        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Get, "/v1/status"),
                     (HttpMethod.Post, "/v1/builds"),
                     (HttpMethod.Get, $"/v1/builds/{opaqueId}"),
                     (HttpMethod.Get, $"/v1/builds/{opaqueId}/artifact"),
                     (HttpMethod.Post, $"/v1/builds/{opaqueId}/start"),
                     (HttpMethod.Delete, $"/v1/builds/{opaqueId}"),
                     (HttpMethod.Post, "/containers/create"),
                     (HttpMethod.Get, "/version"),
                     (HttpMethod.Get, "/unknown")
                 })
        {
            using var request = new HttpRequestMessage(method, path);
            if (method == HttpMethod.Post)
                request.Content = new StringContent("not json", Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.DoesNotContain(fixture.Secret, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        Assert.Equal(0, fixture.Sandbox.PrepareCalls);
    }

    [Fact]
    public async Task StatusDisclosesOnlyTheFixedExecutorIdentityNotCredentialsOrHostPaths()
    {
        await using var fixture = await BrokerFixture.Start();
        using var response = await fixture.Client.GetAsync("/v1/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.True(json.RootElement.GetProperty("isReady").GetBoolean());
        Assert.Equal(WorkerImage, json.RootElement.GetProperty("workerImageId").GetString());
        Assert.Equal(ProxyImage, json.RootElement.GetProperty("proxyImageId").GetString());
        Assert.False(string.IsNullOrEmpty(json.RootElement.GetProperty("instanceId").GetString()));
        Assert.DoesNotContain(fixture.Secret, body, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Root, body, StringComparison.Ordinal);
        Assert.DoesNotContain("docker.sock", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecretsInQueryCookiesOrAmbiguousAuthorizationHeadersAreNotCredentials()
    {
        await using var fixture = await BrokerFixture.Start();
        using var client = fixture.NewClient(authenticated: false);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", "token=" + fixture.Secret);
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Build-Broker-Token", fixture.Secret);
        using (var response = await client.GetAsync("/v1/status?token=" + fixture.Secret))
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", ["Bearer " + fixture.Secret, "Bearer wrong"]);
        using var ambiguous = await client.GetAsync("/v1/status");
        Assert.Equal(HttpStatusCode.Unauthorized, ambiguous.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    [InlineData(" aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void InvalidSecretFilesFailClosedAtStartup(string secret)
    {
        var directory = Path.Combine(Path.GetTempPath(), "pb-broker-secret-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "token");
        try
        {
            File.WriteAllText(path, secret);
            Assert.Throws<InvalidOperationException>(() => new BrokerAuthentication(new BuildBrokerSettings(path, TimeSpan.FromMinutes(45))));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("runtime", "\"runc\"")]
    [InlineData("image", "\"alpine:latest\"")]
    [InlineData("workerImageId", "\"sha256:bad\"")]
    [InlineData("workerImage", "\"btcpayserver/btcpayserver-plugin-builder-worker:v1.0.76\"")]
    [InlineData("proxyImage", "\"btcpayserver/btcpayserver-plugin-builder-proxy:v1.0.76\"")]
    [InlineData("mounts", "[\"/:/host\"]")]
    [InlineData("privileged", "true")]
    [InlineData("capAdd", "[\"SYS_ADMIN\"]")]
    [InlineData("command", "[\"sh\",\"-c\",\"id\"]")]
    [InlineData("environment", "{\"DOCKER_HOST\":\"tcp://attacker:2375\"}")]
    [InlineData("network", "\"host\"")]
    [InlineData("memory", "\"64g\"")]
    [InlineData("pidsLimit", "-1")]
    [InlineData("buildTimeout", "86400000")]
    [InlineData("scratchRoot", "\"/\"")]
    [InlineData("outputPath", "\"/etc/cron.d/build\"")]
    public async Task EvenAuthenticatedRequestsCannotAddDockerOrFilesystemControls(string property, string value)
    {
        await using var fixture = await BrokerFixture.Start();
        var payload = JsonSerializer.Serialize(ValidRequest());
        payload = payload[..^1] + ",\"" + property + "\":" + value + "}";
        using var response = await fixture.PostRaw(payload);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, fixture.Sandbox.PrepareCalls);
    }

    [Theory]
    [InlineData("gitRepository", "http://github.com/owner/plugin")]
    [InlineData("gitRepository", "https://127.0.0.1/owner/plugin")]
    [InlineData("gitRepository", "https://169.254.169.254/latest/meta-data")]
    [InlineData("gitRepository", "https://github.com.evil.test/owner/plugin")]
    [InlineData("gitRepository", "https://gitlab.local/owner/plugin")]
    [InlineData("gitRepository", "https://secret@github.com/owner/plugin")]
    [InlineData("gitRepository", "https://github.com:444/owner/plugin")]
    [InlineData("gitRepository", "https://github.com/owner/plugin?token=secret")]
    [InlineData("gitRepository", "https://github.com/owner/../plugin")]
    [InlineData("gitRepository", "https://github.com/owner/%2e%2e/plugin")]
    [InlineData("pluginSlug", "../../host")]
    [InlineData("pluginSlug", "valid-plugin\n")]
    [InlineData("pluginSlug", "UPPERCASE")]
    [InlineData("pluginSlug", "a")]
    [InlineData("pluginDir", "/etc")]
    [InlineData("pluginDir", "../../etc")]
    [InlineData("pluginDir", "src/../plugin")]
    [InlineData("pluginDir", "src\nplugin")]
    [InlineData("gitRef", "main\n--upload-pack=evil")]
    [InlineData("gitRef", "--upload-pack=evil")]
    [InlineData("buildConfig", "Release /p:Run=evil")]
    [InlineData("buildConfig", "Release\n")]
    [InlineData("buildConfig", "Release\r")]
    public async Task InvalidBuildIdentifiersAndSourcesAreRejectedBeforeTheSandbox(string field, string value)
    {
        await using var fixture = await BrokerFixture.Start();
        var payload = ValidRequest();
        payload[field] = value;
        using var response = await fixture.Client.PostAsJsonAsync("/v1/builds", payload);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, fixture.Sandbox.PrepareCalls);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"pluginSlug\":null}")]
    [InlineData("{\"pluginSlug\":\"valid-plugin\",\"buildId\":-1}")]
    [InlineData("{\"pluginSlug\":\"valid-plugin\",\"buildId\":9223372036854775808}")]
    [InlineData("{\"pluginSlug\":\"valid-plugin\",\"buildId\":1.5}")]
    [InlineData("{\"gitRepository\":\"https://github.com/owner/plugin\",}")]
    public async Task MalformedOrIncompleteJsonNeverStartsAWorker(string body)
    {
        await using var fixture = await BrokerFixture.Start();
        using var response = await fixture.PostRaw(body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, fixture.Sandbox.PrepareCalls);
    }

    [Fact]
    public async Task NegativeBuildNumberIsRejectedWithAnOtherwiseValidRequest()
    {
        await using var fixture = await BrokerFixture.Start();
        using var response = await fixture.Client.PostAsJsonAsync("/v1/builds", ValidRequest(-1));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, fixture.Sandbox.PrepareCalls);
    }

    [Theory]
    [InlineData("pluginSlug")]
    [InlineData("buildId")]
    [InlineData("gitRepository")]
    public async Task RequiredBuildPropertiesCannotBeSilentlyReplacedByDeserializerDefaults(string missingProperty)
    {
        await using var fixture = await BrokerFixture.Start();
        var request = ValidRequest();
        Assert.True(request.Remove(missingProperty));
        using var response = await fixture.Client.PostAsJsonAsync("/v1/builds", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, fixture.Sandbox.PrepareCalls);
    }

    [Fact]
    public async Task OversizedBodyIsRejectedWithoutParsingOrPreparingAWorker()
    {
        await using var fixture = await BrokerFixture.Start();
        using var response = await fixture.PostRaw(new string(' ', 128 * 1024) + JsonSerializer.Serialize(ValidRequest()));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, fixture.Sandbox.PrepareCalls);
    }

    [Fact]
    public async Task ChunkedBodyCannotBypassTheRequestSizeLimit()
    {
        await using var fixture = await BrokerFixture.Start();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/builds")
        {
            Content = new ChunkedJsonContent(new string(' ', 128 * 1024) + JsonSerializer.Serialize(ValidRequest()))
        };
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, fixture.Sandbox.PrepareCalls);
    }

    [Fact]
    public async Task AuthenticatedDockerApiPathsAreNotForwardedToTheEngine()
    {
        await using var fixture = await BrokerFixture.Start();
        foreach (var path in new[] { "/containers/create", "/v1.47/containers/create", "/images/create", "/exec/test/start", "/v1/builds" })
        {
            using var response = await fixture.Client.GetAsync(path);
            Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
                $"Unexpected response on {path}: {response.StatusCode}");
        }
        Assert.Equal(0, fixture.Sandbox.PrepareCalls);
    }

    [Theory]
    [InlineData("not-a-lease")]
    [InlineData("11111111111111111111111111111111111")]
    [InlineData("%2e%2e%2fstatus")]
    [InlineData("%2fetc%2fpasswd")]
    [InlineData("00000000000000000000000000000000")]
    public async Task UnrecognizedOrTraversalLeaseIdsCannotReadOrDeleteAnything(string id)
    {
        await using var fixture = await BrokerFixture.Start();
        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Get, $"/v1/builds/{id}"),
                     (HttpMethod.Delete, $"/v1/builds/{id}"),
                     (HttpMethod.Post, $"/v1/builds/{id}/start")
                 })
        {
            using var request = new HttpRequestMessage(method, path);
            using var response = await fixture.Client.SendAsync(request);
            Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest ||
                        (method == HttpMethod.Delete && id == new string('0', 32) && response.StatusCode == HttpStatusCode.NoContent),
                $"Unexpected response for lease {id}: {response.StatusCode}");
        }
        Assert.Equal(0, fixture.Sandbox.PrepareCalls);
        Assert.True(File.Exists(fixture.TokenPath));
    }

    [Fact]
    public async Task ParallelClientsCannotAllocateMoreThanTwoLeases()
    {
        await using var fixture = await BrokerFixture.Start();
        var submissions = await Task.WhenAll(Enumerable.Range(0, 12).Select(async id =>
        {
            using var client = fixture.NewClient();
            using var response = await client.PostAsJsonAsync("/v1/builds", ValidRequest(id));
            var lease = response.StatusCode == HttpStatusCode.Accepted ? await LeaseId(response) : null;
            return (response.StatusCode, Lease: lease);
        }));
        var accepted = submissions.Where(result => result.StatusCode == HttpStatusCode.Accepted).ToArray();
        Assert.Equal(2, accepted.Length);
        Assert.All(submissions.Where(result => result.Lease is null), result =>
            Assert.Equal(HttpStatusCode.TooManyRequests, result.StatusCode));
        await Eventually(() => fixture.Sandbox.PrepareCalls == 2);
        Assert.Equal(2, fixture.Sandbox.PrepareCalls);
        Assert.NotEqual(accepted[0].Lease, accepted[1].Lease);
        Assert.All(accepted, result => Assert.Matches("\\A[0-9a-f]{32}\\z", result.Lease!));
        foreach (var lease in accepted)
        {
            await fixture.WaitForPrepared(lease.Lease!);
            using var deleted = await fixture.Client.DeleteAsync($"/v1/builds/{lease.Lease}");
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }
        Assert.Equal(2, fixture.Sandbox.DisposalCount);
    }

    [Fact]
    public async Task PreparationRequiresOneExplicitStartAndRejectsPrematureOrRepeatedStart()
    {
        await using var fixture = await BrokerFixture.Start();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Sandbox.BeforePrepare = () => entered.TrySetResult();
        fixture.Sandbox.PreparationBlockedUntil = release.Task;
        var lease = await fixture.Submit(90);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var preparing = await fixture.Client.GetFromJsonAsync<BrokerBuildStatus>($"/v1/builds/{lease}");
            Assert.Equal("preparing", preparing!.State);
            Assert.Empty(preparing.Logs);
            using var premature = await fixture.Client.PostAsync($"/v1/builds/{lease}/start", null);
            Assert.Equal(HttpStatusCode.Conflict, premature.StatusCode);
            Assert.Empty(fixture.Sandbox.Prepared);
        }
        finally { release.TrySetResult(); }

        await fixture.WaitForPrepared(lease);
        var prepared = await fixture.Client.GetFromJsonAsync<BrokerBuildStatus>($"/v1/builds/{lease}");
        Assert.Equal("prepared", prepared!.State);
        Assert.Equal(0, prepared.NextCursor);
        Assert.Empty(prepared.Logs);
        Assert.Null(prepared.Result);
        Assert.Null(prepared.Error);
        Assert.False(fixture.Sandbox.Prepared[90].Started.Task.IsCompleted);
        await fixture.StartBuild(lease);
        await fixture.Sandbox.WaitForStarted(90);
        using var repeated = await fixture.Client.PostAsync($"/v1/builds/{lease}/start", null);
        Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);
    }

    [Theory]
    [InlineData("body")]
    [InlineData("chunked")]
    [InlineData("query")]
    public async Task StartAcceptsNoBodyOrCallerSelectedParameters(string input)
    {
        await using var fixture = await BrokerFixture.Start();
        var lease = await fixture.Submit(91);
        await fixture.WaitForPrepared(lease);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/v1/builds/{lease}/start" + (input == "query" ? "?runtime=runc" : ""));
        if (input == "body") request.Content = new StringContent("{\"runtime\":\"runc\"}");
        if (input == "chunked") request.Content = new ChunkedJsonContent("{\"runtime\":\"runc\"}");
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(fixture.Sandbox.Prepared[91].Started.Task.IsCompleted);
        await fixture.StartBuild(lease);
        await fixture.Sandbox.WaitForStarted(91);
    }

    [Theory]
    [InlineData("delete", false)]
    [InlineData("delete", true)]
    [InlineData("expiry", false)]
    [InlineData("expiry", true)]
    [InlineData("shutdown", false)]
    [InlineData("shutdown", true)]
    public async Task UnstartedLeaseIsCleanedOnDeleteExpiryOrShutdown(string cause, bool duringPreparation)
    {
        await using var fixture = await BrokerFixture.Start(cause == "expiry" ? TimeSpan.FromSeconds(2) : null);
        if (duringPreparation)
            fixture.Sandbox.PreparationBlockedUntil = new TaskCompletionSource().Task;
        var lease = await fixture.Submit(92);
        if (duringPreparation)
            await Eventually(() => fixture.Sandbox.PrepareCalls == 1);
        else
            await fixture.WaitForPrepared(lease);

        if (cause == "delete")
        {
            using var deleted = await fixture.Client.DeleteAsync($"/v1/builds/{lease}");
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }
        if (cause == "shutdown")
            await fixture.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        else
            await Eventually(async () =>
            {
                using var response = await fixture.Client.GetAsync($"/v1/builds/{lease}");
                return response.StatusCode == HttpStatusCode.NotFound;
            }, TimeSpan.FromSeconds(10));

        if (duringPreparation)
            Assert.Empty(fixture.Sandbox.Prepared);
        else
        {
            Assert.True(fixture.Sandbox.Prepared[92].IsDisposed);
            Assert.False(fixture.Sandbox.Prepared[92].Started.Task.IsCompleted);
            Assert.Equal(1, fixture.Sandbox.DisposalCount);
        }
    }

    [Fact]
    public async Task CancellingOneLeaseDoesNotCancelAnotherAndReleasesOnlyItsOwnSlot()
    {
        await using var fixture = await BrokerFixture.Start();
        var first = await fixture.Submit(10);
        var second = await fixture.Submit(11);
        await fixture.StartBuild(first);
        await fixture.StartBuild(second);
        await fixture.Sandbox.WaitForStarted(10);
        await fixture.Sandbox.WaitForStarted(11);

        using (var deleted = await fixture.Client.DeleteAsync($"/v1/builds/{first}"))
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.True(fixture.Sandbox.Prepared[10].CancellationObserved);
        Assert.True(fixture.Sandbox.Prepared[10].IsDisposed);
        Assert.False(fixture.Sandbox.Prepared[11].CancellationObserved);
        Assert.False(fixture.Sandbox.Prepared[11].IsDisposed);
        using (var removed = await fixture.Client.GetAsync($"/v1/builds/{first}"))
            Assert.Equal(HttpStatusCode.NotFound, removed.StatusCode);

        var third = await fixture.Submit(12);
        Assert.NotEqual(first, third);
        await fixture.StartBuild(third);
        await fixture.Sandbox.WaitForStarted(12);
        using var thirdDelete = await fixture.Client.DeleteAsync($"/v1/builds/{third}");
        using var secondDelete = await fixture.Client.DeleteAsync($"/v1/builds/{second}");
        Assert.Equal(HttpStatusCode.NoContent, thirdDelete.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, secondDelete.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("00000000000000000000000000000000")]
    public async Task AClientFromAnotherBrokerInstanceCannotReadStartDownloadOrDeleteThisLease(string? instance)
    {
        await using var fixture = await BrokerFixture.Start();
        var lease = await fixture.Submit(15);
        await fixture.WaitForPrepared(lease);
        using var staleClient = fixture.NewClient();
        staleClient.DefaultRequestHeaders.Remove(InstanceHeader);
        if (instance is not null)
            staleClient.DefaultRequestHeaders.Add(InstanceHeader, instance);
        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Get, $"/v1/builds/{lease}"),
                     (HttpMethod.Get, $"/v1/builds/{lease}/artifact"),
                     (HttpMethod.Post, $"/v1/builds/{lease}/start"),
                     (HttpMethod.Delete, $"/v1/builds/{lease}"),
                     (HttpMethod.Delete, "/v1/builds/11111111111111111111111111111111")
                 })
        {
            using var request = new HttpRequestMessage(method, path);
            using var response = await staleClient.SendAsync(request);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }
        Assert.False(fixture.Sandbox.Prepared[15].CancellationObserved);
        Assert.False(fixture.Sandbox.Prepared[15].Started.Task.IsCompleted);
        Assert.False(fixture.Sandbox.Prepared[15].IsDisposed);
    }

    [Fact]
    public async Task LeaseExpirationCancelsAndCleansAnAbandonedBuildWithoutAnApiClient()
    {
        await using var fixture = await BrokerFixture.Start(TimeSpan.FromSeconds(2));
        var lease = await fixture.Submit(20);
        await fixture.StartBuild(lease);
        await fixture.Sandbox.WaitForStarted(20);
        await Eventually(() => fixture.Sandbox.Prepared[20].IsDisposed, TimeSpan.FromSeconds(10));
        Assert.True(fixture.Sandbox.Prepared[20].CancellationObserved);
        // The cancellation deadline stops the worker before the periodic reaper drops
        // its status record. Reads here observe that cleanup; they do not trigger it.
        await Eventually(async () =>
        {
            using var response = await fixture.Client.GetAsync($"/v1/builds/{lease}");
            return response.StatusCode == HttpStatusCode.NotFound;
        }, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task FailedCleanupIsNeverReportedAsSuccessfulDeletion()
    {
        await using var fixture = await BrokerFixture.Start();
        fixture.Sandbox.FailDisposal = true;
        var lease = await fixture.Submit(30);
        await fixture.StartBuild(lease);
        await fixture.Sandbox.WaitForStarted(30);

        using var response = await fixture.Client.DeleteAsync($"/v1/builds/{lease}");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var status = await fixture.Client.GetAsync($"/v1/builds/{lease}");
        Assert.NotEqual(HttpStatusCode.NotFound, status.StatusCode);
        using var secondDelete = await fixture.Client.DeleteAsync($"/v1/builds/{lease}");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, secondDelete.StatusCode);
        using var statusResponse = await fixture.Client.GetAsync("/v1/status");
        using var state = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync());
        Assert.False(state.RootElement.GetProperty("isReady").GetBoolean());
        using var newBuild = await fixture.Client.PostAsJsonAsync("/v1/builds", ValidRequest(31));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, newBuild.StatusCode);
        Assert.Equal(1, fixture.Sandbox.PrepareCalls);
        fixture.Sandbox.FailDisposal = false;
    }

    [Fact]
    public async Task FailedPartialPreparationCleanupCannotBecomeASuccessfulDeleteWithoutAPreparedHandle()
    {
        await using var fixture = await BrokerFixture.Start();
        fixture.Sandbox.PrepareFailure = new BuildServiceException("Partial preparation cleanup could not be confirmed.");
        fixture.Sandbox.BeforePrepare = () => fixture.Executor.MarkUnavailable("Partial preparation cleanup failed.");
        var lease = await fixture.Submit(32);
        await Eventually(async () =>
        {
            using var response = await fixture.Client.GetAsync($"/v1/builds/{lease}");
            using var status = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return status.RootElement.GetProperty("state").GetString() == "failed";
        });
        Assert.Empty(fixture.Sandbox.Prepared);
        Assert.False(fixture.Executor.Snapshot.IsReady);
        using var deleted = await fixture.Client.DeleteAsync($"/v1/builds/{lease}");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, deleted.StatusCode);
        using var retained = await fixture.Client.GetAsync($"/v1/builds/{lease}");
        Assert.Equal(HttpStatusCode.OK, retained.StatusCode);
        using var refused = await fixture.Client.PostAsJsonAsync("/v1/builds", ValidRequest(33));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal(1, fixture.Sandbox.PrepareCalls);
    }

    [Theory]
    [InlineData("running")]
    [InlineData("succeeded")]
    [InlineData("failed")]
    public async Task WorkerLogPaginationIsBoundedAndDefersTerminalStateUntilAllLogsAreRead(string buildState)
    {
        await using var fixture = await BrokerFixture.Start();
        fixture.Sandbox.LogLines = Enumerable.Range(0, 200).Select(i => $"{i:D4}:" + new string('x', 1024)).ToArray();
        fixture.Sandbox.CompleteImmediately = buildState == "succeeded";
        fixture.Sandbox.RunFailure = buildState == "failed" ? new InvalidOperationException("Test build failed.") : null;
        var expectedLines = fixture.Sandbox.LogLines
            .Concat(buildState == "succeeded" ? new[] { "Test plugin compiled." } : Array.Empty<string>()).ToArray();
        var lease = await fixture.Submit(35);
        await fixture.StartBuild(lease);
        await fixture.Sandbox.WaitForStarted(35);
        await Eventually(() => fixture.Sandbox.Prepared[35].LogsWritten);
        // Observe completion past the large log prefix before reading from zero.
        // Otherwise the pagination assertions could all run before the build ends.
        await Eventually(async () =>
        {
            var status = await fixture.Client.GetFromJsonAsync<BrokerBuildStatus>(
                $"/v1/builds/{lease}?cursor={fixture.Sandbox.LogLines.Length}");
            return status!.State == buildState;
        });
        var cursor = 0;
        var received = new List<string>();
        do
        {
            var status = await fixture.Client.GetFromJsonAsync<BrokerBuildStatus>($"/v1/builds/{lease}?cursor={cursor}");
            Assert.NotNull(status);
            var lines = status.Logs;
            Assert.InRange(lines.Sum(line => Encoding.UTF8.GetByteCount(line) + 1), 0, 64 * 1024);
            var nextCursor = status.NextCursor;
            Assert.Equal(cursor + lines.Length, nextCursor);
            var expectedState = nextCursor < expectedLines.Length ? "running" : buildState;
            Assert.Equal(expectedState, status.State);
            Assert.Equal(expectedState == "succeeded", status.Result is not null);
            if (expectedState == "failed")
                Assert.False(string.IsNullOrEmpty(status.Error));
            else
                Assert.Null(status.Error);
            if (nextCursor == cursor)
                break;
            received.AddRange(lines);
            cursor = nextCursor;
            Assert.InRange(cursor, 1, expectedLines.Length);
        } while (true);
        Assert.Equal(expectedLines, received);
    }

    [Fact]
    public async Task SameLengthArtifactMutationCannotServeBytesThatDisagreeWithTheAdvertisedHash()
    {
        await using var fixture = await BrokerFixture.Start();
        fixture.Sandbox.CompleteImmediately = true;
        var lease = await fixture.Submit(41);
        await fixture.StartBuild(lease);
        using var status = await fixture.WaitForResult(lease);
        using var unauthenticated = fixture.NewClient(authenticated: false);
        using var denied = await unauthenticated.GetAsync($"/v1/builds/{lease}/artifact");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        var staged = fixture.Sandbox.Prepared[41];
        if (File.Exists(staged.ArtifactPath))
            await File.WriteAllBytesAsync(staged.ArtifactPath, Enumerable.Repeat((byte)'x', FakePrepared.ArtifactBytes.Length).ToArray());

        using var response = await fixture.Client.GetAsync($"/v1/builds/{lease}/artifact");
        if (response.IsSuccessStatusCode)
        {
            // A broker-owned immutable snapshot may legitimately preserve the original bytes.
            var bytes = await response.Content.ReadAsByteArrayAsync();
            Assert.Equal(status.RootElement.GetProperty("result").GetProperty("artifactSha256").GetString(),
                Convert.ToHexStringLower(SHA256.HashData(bytes)));
            Assert.Equal(FakePrepared.ArtifactBytes, bytes);
        }
        else
        {
            Assert.True(response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.ServiceUnavailable,
                $"Unexpected changed-artifact response: {response.StatusCode}");
        }
    }

    [Fact]
    public async Task AStagedArtifactSymlinkCannotTurnTheDownloadRouteIntoAHostFileReader()
    {
        await using var fixture = await BrokerFixture.Start();
        fixture.Sandbox.CompleteImmediately = true;
        var lease = await fixture.Submit(42);
        await fixture.StartBuild(lease);
        using var status = await fixture.WaitForResult(lease);
        var staged = fixture.Sandbox.Prepared[42];
        if (File.Exists(staged.ArtifactPath))
        {
            File.Delete(staged.ArtifactPath);
            File.CreateSymbolicLink(staged.ArtifactPath, fixture.TokenPath);
        }
        using var response = await fixture.Client.GetAsync($"/v1/builds/{lease}/artifact");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.DoesNotContain(fixture.Secret, Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        if (response.IsSuccessStatusCode)
            Assert.Equal(FakePrepared.ArtifactBytes, bytes);
        else
            Assert.True(response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task BuildFailureDoesNotExposeInternalExceptionDetailsOrCreateAnArtifact()
    {
        await using var fixture = await BrokerFixture.Start();
        fixture.Sandbox.RunFailure = new InvalidOperationException($"private {fixture.Secret} {fixture.Root}/docker.sock");
        var lease = await fixture.Submit(50);
        await fixture.StartBuild(lease);
        await Eventually(() => fixture.Sandbox.Prepared.TryGetValue(50, out var build) && build.IsDisposed);
        using var response = await fixture.Client.GetAsync($"/v1/builds/{lease}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(fixture.Secret, body, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Root, body, StringComparison.Ordinal);
        using var artifact = await fixture.Client.GetAsync($"/v1/builds/{lease}/artifact");
        Assert.False(artifact.IsSuccessStatusCode);
    }

    [Theory]
    [InlineData("Plugin build timed out after 00:15:00.", true)]
    [InlineData("Plugin build timed out after 00:00:01.", false)]
    [InlineData("Plugin build timed out after 00:15:00. {private}", false)]
    [InlineData("{private}", false)]
    public async Task ProductionRemoteClientPreservesOnlySafeBuildFailureMessages(string message, bool isPublic)
    {
        await using var fixture = await BrokerFixture.Start();
        message = message.Replace("{private}", $"private {fixture.Secret} {fixture.Root}/docker.sock");
        fixture.Sandbox.RunFailure = new BuildServiceException(message);
        var appExecutor = new BuildExecutorState();
        appExecutor.MarkReady(WorkerImage, ProxyImage);
        using var client = fixture.CreateRemoteSandbox(appExecutor);
        await using var prepared = await client.PrepareAsync(new FullBuildId("valid-plugin", 62), new BuildInfo
        {
            GitRepository = "https://github.com/owner/plugin",
            BuildConfig = "Release"
        });
        var logs = new OutputCapture();

        var error = await Assert.ThrowsAsync<BuildServiceException>(() =>
            prepared.RunAndStageAsync(logs).WaitAsync(TimeSpan.FromSeconds(8)));

        Assert.Equal(isPublic ? message : "Plugin build failed in the isolated executor.", error.Message);
        Assert.DoesNotContain(fixture.Secret, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Root, error.ToString(), StringComparison.Ordinal);
        Assert.Empty(logs.Lines);
        await prepared.DisposeAsync();
        Assert.True(fixture.Sandbox.Prepared[62].IsDisposed);
        Assert.Equal(1, fixture.Sandbox.DisposalCount);
        var stagingRoot = Path.Combine(fixture.Root, "web-data", "broker-staging");
        if (Directory.Exists(stagingRoot))
            Assert.Empty(Directory.EnumerateFileSystemEntries(stagingRoot));
        Assert.True((await client.GetStatusAsync(CancellationToken.None)).IsReady);
    }

    [Fact]
    public async Task ProductionRemoteClientBuildsDownloadsVerifiesAndCleansThroughTheActualHttpBroker()
    {
        await using var fixture = await BrokerFixture.Start();
        fixture.Sandbox.CompleteImmediately = true;
        var appExecutor = new BuildExecutorState();
        using var client = fixture.CreateRemoteSandbox(appExecutor);
        var status = await client.GetStatusAsync(CancellationToken.None);
        Assert.True(status.IsReady);
        appExecutor.MarkReady(status.WorkerImageId!, status.ProxyImageId!);
        await using var prepared = await client.PrepareAsync(new FullBuildId("valid-plugin", 60), new BuildInfo
        {
            GitRepository = "https://github.com/owner/plugin",
            GitRef = "main",
            PluginDir = "src/Plugin",
            BuildConfig = "Release"
        });
        var logs = new OutputCapture();
        var staged = await prepared.RunAndStageAsync(logs).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(FakePrepared.ArtifactBytes, await File.ReadAllBytesAsync(Path.Combine(staged.StagingDirectory, "artifact.btcpay")));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(FakePrepared.ArtifactBytes)), staged.BuildEnvironment["buildHash"]!.Value<string>());
        Assert.Equal("https://github.com/owner/plugin", staged.BuildEnvironment["gitRepository"]!.Value<string>());
        Assert.StartsWith(Path.Combine(fixture.Root, "web-data", "broker-staging") + Path.DirectorySeparatorChar,
            staged.StagingDirectory, StringComparison.Ordinal);
        Assert.NotEqual(Path.GetDirectoryName(fixture.Sandbox.Prepared[60].ArtifactPath), staged.StagingDirectory);
        Assert.Equal(["Test plugin compiled."], logs.Lines);
        // The remote resources are already gone when an upload-ready local copy
        // is returned; local disposal must not be the first remote cleanup.
        Assert.True(fixture.Sandbox.Prepared[60].IsDisposed);
        Assert.False(File.Exists(fixture.Sandbox.Prepared[60].ArtifactPath));
        Assert.Equal(1, fixture.Sandbox.DisposalCount);

        await prepared.DisposeAsync();
        Assert.True(fixture.Sandbox.Prepared[60].IsDisposed);
        Assert.False(Directory.Exists(staged.StagingDirectory));
        Assert.Equal(1, fixture.Sandbox.DisposalCount);
        Assert.True((await client.GetStatusAsync(CancellationToken.None)).IsReady);
    }

    [Fact]
    public async Task ProductionRemoteClientCannotReturnArtifactWhenActualBrokerCleanupFails()
    {
        await using var fixture = await BrokerFixture.Start();
        fixture.Sandbox.CompleteImmediately = true;
        fixture.Sandbox.FailDisposal = true;
        var appExecutor = new BuildExecutorState();
        using var client = fixture.CreateRemoteSandbox(appExecutor);
        var status = await client.GetStatusAsync(CancellationToken.None);
        appExecutor.MarkReady(status.WorkerImageId!, status.ProxyImageId!);
        var prepared = await client.PrepareAsync(new FullBuildId("valid-plugin", 61), new BuildInfo
        {
            GitRepository = "https://github.com/owner/plugin",
            BuildConfig = "Release"
        });

        await Assert.ThrowsAsync<BuildServiceException>(() =>
            prepared.RunAndStageAsync(new OutputCapture()).WaitAsync(TimeSpan.FromSeconds(8)));
        Assert.False((await client.GetStatusAsync(CancellationToken.None)).IsReady);
        await Assert.ThrowsAsync<BuildServiceException>(() => prepared.DisposeAsync().AsTask());
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(fixture.Root, "web-data", "broker-staging")));
    }

    [Fact]
    public async Task ProductionRemoteClientCancellationStopsTheWorkerAndConfirmsCleanupOverHttp()
    {
        await using var fixture = await BrokerFixture.Start();
        var appExecutor = new BuildExecutorState();
        using var client = fixture.CreateRemoteSandbox(appExecutor);
        appExecutor.MarkReady(WorkerImage, ProxyImage);
        await using var prepared = await client.PrepareAsync(new FullBuildId("valid-plugin", 61), new BuildInfo
        {
            GitRepository = "https://github.com/owner/plugin",
            GitRef = "main",
            PluginDir = "src/Plugin",
            BuildConfig = "Release"
        });
        Assert.False(fixture.Sandbox.Prepared[61].Started.Task.IsCompleted);
        var execution = prepared.RunAndStageAsync(new OutputCapture());
        await fixture.Sandbox.WaitForStarted(61);
        await prepared.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.True(fixture.Sandbox.Prepared[61].CancellationObserved);
        Assert.True(fixture.Sandbox.Prepared[61].IsDisposed);
        Assert.True(appExecutor.Snapshot.IsReady);
        Assert.True((await client.GetStatusAsync(CancellationToken.None)).IsReady);
    }

    private static Dictionary<string, object?> ValidRequest(long buildId = 1) => new()
    {
        ["pluginSlug"] = "valid-plugin",
        ["buildId"] = buildId,
        ["gitRepository"] = "https://github.com/owner/plugin",
        ["gitRef"] = "main",
        ["pluginDir"] = "src/Plugin",
        ["buildConfig"] = "Release"
    };

    private static async Task<string> LeaseId(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("leaseId").GetString()!;
    }

    private static async Task Eventually(Func<bool> condition, TimeSpan? timeout = null)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < (timeout ?? TimeSpan.FromSeconds(5)), "Condition was not reached within the test deadline.");
            await Task.Delay(20);
        }
    }

    private static async Task Eventually(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var watch = Stopwatch.StartNew();
        while (!await condition())
        {
            Assert.True(watch.Elapsed < (timeout ?? TimeSpan.FromSeconds(5)), "Condition was not reached within the test deadline.");
            await Task.Delay(20);
        }
    }

    internal sealed class BrokerFixture : IAsyncDisposable
    {
        private WebApplication _app = null!;
        private Uri _address = null!;
        private string? _instanceId;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "pb-broker-test-" + Guid.NewGuid().ToString("N"));
        public string Secret { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        public string TokenPath => Path.Combine(Root, "broker.token");
        public FakeSandbox Sandbox { get; private set; } = null!;
        public BuildExecutorState Executor { get; private set; } = null!;
        public HttpClient Client { get; private set; } = null!;

        public static async Task<BrokerFixture> Start(TimeSpan? leaseLifetime = null)
        {
            var fixture = new BrokerFixture();
            Directory.CreateDirectory(fixture.Root);
            await File.WriteAllTextAsync(fixture.TokenPath, fixture.Secret + "\n");
            fixture.Sandbox = new FakeSandbox(fixture.Root);
            var state = new BuildExecutorState();
            state.MarkReady(WorkerImage, ProxyImage);
            fixture.Executor = state;
            fixture._app = BuildBrokerApplication.Create([], builder =>
            {
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Logging.ClearProviders();
                foreach (var registration in builder.Services.Where(descriptor =>
                             descriptor.ServiceType == typeof(IHostedService) &&
                             (descriptor.ImplementationType == typeof(DockerStartupHostedService) ||
                              descriptor.ImplementationType == typeof(BrokerDockerMonitor))).ToArray())
                    builder.Services.Remove(registration);
                builder.Services.RemoveAll<IBuildSandbox>();
                builder.Services.AddSingleton<IBuildSandbox>(fixture.Sandbox);
                builder.Services.RemoveAll<BuildExecutorOptions>();
                builder.Services.AddSingleton(new BuildExecutorOptions());
                builder.Services.RemoveAll<BuildExecutorState>();
                builder.Services.AddSingleton(state);
                builder.Services.RemoveAll<BuildBrokerSettings>();
                builder.Services.AddSingleton(new BuildBrokerSettings(fixture.TokenPath, leaseLifetime ?? TimeSpan.FromMinutes(45)));
            });
            try
            {
                await fixture._app.StartAsync();
                var addresses = fixture._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
                fixture._address = new Uri(Assert.Single(addresses!.Addresses));
                fixture.Client = fixture.NewClient();
                using var status = await fixture.Client.GetAsync("/v1/status");
                Assert.Equal(HttpStatusCode.OK, status.StatusCode);
                using var statusJson = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
                fixture._instanceId = statusJson.RootElement.GetProperty("instanceId").GetString();
                fixture.Client.DefaultRequestHeaders.Add(InstanceHeader, fixture._instanceId);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public HttpClient NewClient(bool authenticated = true)
        {
            var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
            {
                BaseAddress = _address,
                Timeout = TimeSpan.FromSeconds(12)
            };
            if (authenticated)
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Secret);
            if (_instanceId is not null)
                client.DefaultRequestHeaders.Add(InstanceHeader, _instanceId);
            return client;
        }

        public Task<HttpResponseMessage> PostRaw(string content) =>
            Client.PostAsync("/v1/builds", new StringContent(content, Encoding.UTF8, "application/json"));

        public RemoteBuildSandbox CreateRemoteSandbox(BuildExecutorState state, HttpMessageHandler? transport = null)
        {
            var options = new PluginBuilderOptions
            {
                BuildBrokerUrl = _address,
                BuildBrokerTokenFile = TokenPath,
                DataDir = Path.Combine(Root, "web-data")
            };
            return transport is null
                ? new(options, state, NullLogger<RemoteBuildSandbox>.Instance)
                : new(options, state, NullLogger<RemoteBuildSandbox>.Instance, transport);
        }

        public async Task<string> Submit(long buildId)
        {
            using var response = await Client.PostAsJsonAsync("/v1/builds", ValidRequest(buildId));
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.Equal(_instanceId, Assert.Single(response.Headers.GetValues(InstanceHeader)));
            return await LeaseId(response);
        }

        public Task WaitForPrepared(string lease) => Eventually(async () =>
        {
            using var response = await Client.GetAsync($"/v1/builds/{lease}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var status = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return status.RootElement.GetProperty("state").GetString() == "prepared";
        });

        public async Task StartBuild(string lease)
        {
            await WaitForPrepared(lease);
            using var response = await Client.PostAsync($"/v1/builds/{lease}/start", null);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        public async Task<JsonDocument> WaitForResult(string lease)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(5))
            {
                using var response = await Client.GetAsync($"/v1/builds/{lease}");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (json.RootElement.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
                    return json;
                json.Dispose();
                await Task.Delay(20);
            }
            throw new TimeoutException("Broker did not produce an artifact within the test deadline.");
        }

        public async ValueTask DisposeAsync()
        {
            Client?.Dispose();
            if (_app is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _app.StopAsync(timeout.Token);
                await _app.DisposeAsync();
            }
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }

        public Task StopAsync() => _app.StopAsync();
    }

    internal sealed class FakeSandbox(string root) : IBuildSandbox
    {
        private int _prepareCalls;
        private int _disposalCount;
        public int PrepareCalls => Volatile.Read(ref _prepareCalls);
        public int DisposalCount => Volatile.Read(ref _disposalCount);
        public bool CompleteImmediately { get; set; }
        public bool FailDisposal { get; set; }
        public Exception? RunFailure { get; set; }
        public Exception? PrepareFailure { get; set; }
        public Action? BeforePrepare { get; set; }
        public Task? PreparationBlockedUntil { get; set; }
        public Task? DisposalBlockedUntil { get; set; }
        public string ManifestJson { get; set; } = "{\"identifier\":\"Test.Plugin\"}";
        public string[] LogLines { get; set; } = [];
        public ConcurrentDictionary<long, FakePrepared> Prepared { get; } = new();

        public async Task<IPreparedBuild> PrepareAsync(FullBuildId buildId, BuildInfo buildInfo, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _prepareCalls);
            BeforePrepare?.Invoke();
            if (PreparationBlockedUntil is { } preparation)
                await preparation.WaitAsync(cancellationToken);
            if (PrepareFailure is not null)
                throw PrepareFailure;
            var prepared = new FakePrepared(this, Path.Combine(root, Guid.NewGuid().ToString("N")), buildInfo, cancellationToken);
            Assert.True(Prepared.TryAdd(buildId.BuildId, prepared));
            return prepared;
        }

        public void RecordDisposal() => Interlocked.Increment(ref _disposalCount);
        public async Task WaitForStarted(long buildId)
        {
            await Eventually(() => Prepared.ContainsKey(buildId));
            await Prepared[buildId].Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    internal sealed class FakePrepared(FakeSandbox owner, string staging, BuildInfo buildInfo, CancellationToken cancellationToken) : IPreparedBuild
    {
        public static readonly byte[] ArtifactBytes = Encoding.UTF8.GetBytes("PK\u0003\u0004broker-test-immutable-artifact");
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CancellationObserved { get; private set; }
        public bool IsDisposed { get; private set; }
        public bool LogsWritten { get; private set; }
        public string ArtifactPath => Path.Combine(staging, "artifact.btcpay");

        public async Task<StagedBuildOutput> RunAndStageAsync(IOutputCapture buildOutput)
        {
            Started.TrySetResult();
            foreach (var line in owner.LogLines)
                buildOutput.AddLine(line);
            LogsWritten = true;
            if (owner.RunFailure is not null)
                throw owner.RunFailure;
            if (!owner.CompleteImmediately)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    CancellationObserved = true;
                    throw;
                }
            }
            Directory.CreateDirectory(staging);
            await File.WriteAllBytesAsync(ArtifactPath, ArtifactBytes);
            var hash = Convert.ToHexStringLower(SHA256.HashData(ArtifactBytes));
            var environment = new JObject
            {
                ["assemblyName"] = "Test.Plugin",
                ["gitCommit"] = new string('a', 40),
                ["gitCommitDate"] = "2026-09-10T00:00:00Z",
                ["buildDate"] = "2026-09-10T00:00:00Z",
                ["buildHash"] = hash,
                ["gitRepository"] = buildInfo.GitRepository,
                ["gitRef"] = buildInfo.GitRef,
                ["pluginDir"] = buildInfo.PluginDir,
                ["buildConfig"] = buildInfo.BuildConfig
            };
            buildOutput.AddLine("Test plugin compiled.");
            return new StagedBuildOutput(environment, owner.ManifestJson, "Test.Plugin", staging);
        }

        public async ValueTask DisposeAsync()
        {
            if (owner.DisposalBlockedUntil is { } disposal)
                await disposal;
            if (owner.FailDisposal)
                throw new BuildServiceException("The trusted scratch directory could not be cleaned.");
            if (!IsDisposed)
            {
                IsDisposed = true;
                owner.RecordDisposal();
                if (Directory.Exists(staging))
                    Directory.Delete(staging, recursive: true);
            }
        }
    }

    private sealed class ChunkedJsonContent : HttpContent
    {
        private readonly byte[] _bytes;
        public ChunkedJsonContent(string json)
        {
            _bytes = Encoding.UTF8.GetBytes(json);
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(_bytes);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
