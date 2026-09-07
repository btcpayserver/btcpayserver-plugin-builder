using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PluginBuilder.Services;
using Xunit;

namespace PluginBuilder.Tests;

public class AzureStagedUploadContractTests
{
    [Fact]
    public async Task UploadsOnlyCanonicalArtifactThroughSdkWithNonOverwriteCondition()
    {
        await using var server = await BlobServer.Start();
        using var staging = new StagingDirectory();
        var bytes = "canonical artifact"u8.ToArray();
        await File.WriteAllBytesAsync(staging.Artifact, bytes);
        await File.WriteAllTextAsync(Path.Combine(staging.Path, "manifest.json"), "not uploaded");
        await File.WriteAllTextAsync(Path.Combine(staging.Path, "other.btcpay"), "not uploaded either");

        var url = await server.CreateClient().UploadStagedArtifact(staging.Path, "example/7/Example.btcpay");

        var request = Assert.Single(server.Requests);
        Assert.Equal("/satoshi/artifacts/example/7/Example.btcpay", request.Path);
        Assert.Equal("*", request.IfNoneMatch);
        Assert.Equal("application/zip", request.ContentType);
        Assert.Equal(bytes, request.Body);
        Assert.Equal(server.Address + request.Path, url);
    }

    [Fact]
    public async Task ExistingArtifactIsNeverOverwritten()
    {
        await using var server = await BlobServer.Start();
        using var staging = new StagingDirectory();
        await File.WriteAllTextAsync(staging.Artifact, "first artifact");
        var client = server.CreateClient();
        await client.UploadStagedArtifact(staging.Path, "example/7/Example.btcpay");
        await File.WriteAllTextAsync(staging.Artifact, "replacement artifact");

        await Assert.ThrowsAsync<AzureStorageClientException>(() =>
            client.UploadStagedArtifact(staging.Path, "example/7/Example.btcpay"));

        Assert.Equal("first artifact"u8.ToArray(), server.StoredArtifact);
        Assert.Equal(2, server.Requests.Count);
        Assert.All(server.Requests, request => Assert.Equal("*", request.IfNoneMatch));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("oversized")]
    [InlineData("directory")]
    [InlineData("symlink")]
    [InlineData("broken-symlink")]
    [InlineData("fifo")]
    public async Task RejectsInvalidArtifactsBeforeMakingStorageRequests(string kind)
    {
        if (OperatingSystem.IsWindows() && kind is "symlink" or "broken-symlink" or "fifo")
            return;
        await using var server = await BlobServer.Start();
        using var staging = new StagingDirectory();
        switch (kind)
        {
            case "empty":
                await File.WriteAllBytesAsync(staging.Artifact, []);
                break;
            case "oversized":
                using (var file = File.Create(staging.Artifact))
                    file.SetLength(256L * 1024 * 1024 + 1);
                break;
            case "directory":
                Directory.CreateDirectory(staging.Artifact);
                break;
            case "symlink":
            case "broken-symlink":
                var target = Path.Combine(staging.Path, "secret.txt");
                if (kind == "symlink")
                    await File.WriteAllTextAsync(target, "must not be uploaded");
                File.CreateSymbolicLink(staging.Artifact, target);
                break;
            case "fifo":
                using (var process = Process.Start(new ProcessStartInfo("mkfifo")
                       { ArgumentList = { staging.Artifact }, UseShellExecute = false })!)
                {
                    await process.WaitForExitAsync();
                    Assert.Equal(0, process.ExitCode);
                }
                break;
        }

        await Assert.ThrowsAsync<AzureStorageClientException>(() =>
            server.CreateClient().UploadStagedArtifact(staging.Path, "example/7/Example.btcpay")
                .WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task RejectsSymbolicLinkStagingDirectory()
    {
        if (OperatingSystem.IsWindows())
            return;
        await using var server = await BlobServer.Start();
        using var staging = new StagingDirectory();
        await File.WriteAllTextAsync(staging.Artifact, "test artifact");
        var link = Path.Combine(staging.Path, "linked-staging");
        Directory.CreateSymbolicLink(link, staging.Path);
        try
        {
            await Assert.ThrowsAsync<AzureStorageClientException>(() =>
                server.CreateClient().UploadStagedArtifact(link, "example/7/Example.btcpay"));
            Assert.Empty(server.Requests);
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public async Task CancellationStopsAnInFlightStorageRequest()
    {
        await using var server = await BlobServer.Start(delayResponse: true);
        using var staging = new StagingDirectory();
        await File.WriteAllTextAsync(staging.Artifact, "test artifact");
        using var cancellation = new CancellationTokenSource();
        var upload = server.CreateClient().UploadStagedArtifact(
            staging.Path, "example/7/Example.btcpay", cancellation.Token);
        await server.RequestReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(server.StoredArtifact);
    }

    [Fact]
    public async Task StorageFailureDoesNotExposeServerResponseInBuildError()
    {
        await using var server = await BlobServer.Start(rejectRequest: true);
        using var staging = new StagingDirectory();
        await File.WriteAllTextAsync(staging.Artifact, "test artifact");

        var error = await Assert.ThrowsAsync<AzureStorageClientException>(() =>
            server.CreateClient().UploadStagedArtifact(staging.Path, "example/7/Example.btcpay"));

        Assert.Equal("Impossible to upload the staged plugin artifact", error.Message);
        Assert.DoesNotContain("secret-sentinel", error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    private sealed class StagingDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pb-sdk-upload-{Guid.NewGuid():N}");
        public string Artifact => System.IO.Path.Combine(Path, "artifact.btcpay");

        public StagingDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    internal sealed record BlobRequest(string Path, string IfNoneMatch, string ContentType, byte[] Body);

    // A loopback-only HTTP endpoint exercises the real Azure SDK request body,
    // conditional PUT and cancellation without any cloud account or Docker DNS.
    internal sealed class BlobServer(WebApplication application) : IAsyncDisposable
    {
        public ConcurrentQueue<BlobRequest> Requests { get; } = new();
        public TaskCompletionSource RequestReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public byte[]? StoredArtifact { get; private set; }
        public string Address { get; private set; } = "";

        public static async Task<BlobServer> Start(bool delayResponse = false, bool rejectRequest = false)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            var server = new BlobServer(app);
            app.MapPut("/{**blob}", async (HttpContext context) =>
            {
                using var body = new MemoryStream();
                await context.Request.Body.CopyToAsync(body, context.RequestAborted);
                var request = new BlobRequest(context.Request.Path,
                    context.Request.Headers.IfNoneMatch.ToString(),
                    context.Request.ContentType ?? "", body.ToArray());
                server.Requests.Enqueue(request);
                server.RequestReceived.TrySetResult();
                if (delayResponse)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
                    return;
                }
                if (rejectRequest || server.StoredArtifact is not null && request.IfNoneMatch == "*")
                {
                    context.Response.StatusCode = rejectRequest ? 403 : 412;
                    context.Response.ContentType = "application/xml";
                    await context.Response.WriteAsync(
                        "<Error><Code>ConditionNotMet</Code><Message>secret-sentinel</Message></Error>");
                    return;
                }
                server.StoredArtifact = request.Body;
                context.Response.StatusCode = 201;
                context.Response.Headers.ETag = "\"test-etag\"";
                context.Response.Headers.LastModified = DateTimeOffset.UtcNow.ToString("R");
            });
            await app.StartAsync();
            server.Address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
                .Addresses.Single().TrimEnd('/');
            return server;
        }

        public AzureStorageClient CreateClient()
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["STORAGE_CONNECTION_STRING"] = $"BlobEndpoint={Address}/satoshi;AccountName=satoshi;" +
                                                "AccountKey=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="
            }).Build();
            return new AzureStorageClient(configuration);
        }

        public async ValueTask DisposeAsync()
        {
            await application.StopAsync();
            await application.DisposeAsync();
        }
    }
}
