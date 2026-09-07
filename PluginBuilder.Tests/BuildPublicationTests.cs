using System.Collections.Concurrent;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PluginBuilder.Events;
using PluginBuilder.HostedServices;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class BuildPublicationTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishesOnlyAfterFinalScratchCleanupSucceeds(bool failCleanup)
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var storage = await AzureStagedUploadContractTests.BlobServer.Start();
        await using var docker = await DockerBuildSandboxLifecycleTests.FakeDocker.Create(
            failScratchCleanup: failCleanup,
            manifestJson: """{"Identifier":"Example","Name":"Example","Version":"1.0.0"}""");
        for (var slot = 0; slot < DockerBuildSandbox.MaxConcurrentBuilds; slot++)
            Directory.CreateDirectory(DockerBuildSandbox.ScratchSlotPath(docker.Directory, slot));

        await using var tester = Create("BuildPublication");
        tester.ReuseDatabase = false;
        tester.BuildScratchRoot = docker.Directory;
        tester.ConfigureServices = services =>
        {
            // Exercise BuildService, the sandbox and SDK upload, but never start
            // a real executor or contact the shared storage emulator/Git hosts.
            foreach (var service in services.Where(service => service.ServiceType == typeof(IHostedService) &&
                         (service.ImplementationType == typeof(DockerStartupHostedService) ||
                          service.ImplementationType == typeof(AzureStartupHostedService))).ToArray())
                services.Remove(service);
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

        var events = new ConcurrentQueue<(string State, bool ScratchExists)>();
        using var subscription = tester.GetService<EventAggregator>().Subscribe<BuildChanged>(evt =>
        {
            if (evt.FullBuildId == id)
                events.Enqueue((evt.EventName,
                    Directory.EnumerateDirectories(docker.Directory, "pb-build-*", SearchOption.AllDirectories).Any()));
        });

        var error = await Record.ExceptionAsync(() => tester.GetService<BuildService>().Build(id));
        // Prove the action reached a successful real SDK upload before cleanup.
        var upload = Assert.Single(storage.Requests);
        Assert.Equal($"/satoshi/artifacts/{id}/Example.btcpay", upload.Path);
        Assert.Equal("canonical artifact"u8.ToArray(), storage.StoredArtifact);
        Assert.Contains(await docker.ReadCommands(), command =>
            command.StartsWith("container start --attach pb-scratch-clean-", StringComparison.Ordinal));

        var state = await connection.QuerySingleAsync<string>(
            "SELECT state FROM builds WHERE plugin_slug=@slug AND id=@buildId",
            new { slug = slug.ToString(), buildId = id.BuildId });
        var versions = (await connection.QueryAsync<long>(
            "SELECT build_id FROM versions WHERE plugin_slug=@slug", new { slug = slug.ToString() })).ToArray();
        if (failCleanup)
        {
            Assert.Contains("clean up", Assert.IsType<BuildServiceException>(error).Message);
            Assert.Equal(BuildStates.Failed.ToEventName(), state);
            Assert.Empty(versions);
            Assert.DoesNotContain(events, evt => evt.State == BuildStates.Uploaded.ToEventName());
            Assert.Contains(events, evt => evt.State == BuildStates.Failed.ToEventName());
            Assert.False(executor.Snapshot.IsReady);
        }
        else
        {
            Assert.Null(error);
            Assert.Equal(BuildStates.Uploaded.ToEventName(), state);
            Assert.Equal(id.BuildId, Assert.Single(versions));
            var published = Assert.Single(events, evt => evt.State == BuildStates.Uploaded.ToEventName());
            Assert.False(published.ScratchExists);
            Assert.True(executor.Snapshot.IsReady);
        }
    }
}
