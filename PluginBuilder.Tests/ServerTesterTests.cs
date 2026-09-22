using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using PluginBuilder.Configuration;
using PluginBuilder.HostedServices;
using PluginBuilder.Services;
using Xunit;
using Xunit.Abstractions;

using PluginBuilder.Builds.BuildBroker;
using PluginBuilder.Builds.Services;

namespace PluginBuilder.Tests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class ServerTesterTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ServerTesterStartWaitsForBrokerReadinessOnlyWhenMonitorIsPresent(bool withMonitor)
    {
        var directory = Directory.CreateTempSubdirectory("pb-server-readiness-").FullName;
        try
        {
            var tokenPath = Path.Combine(directory, "broker-token");
            await File.WriteAllTextAsync(tokenPath, new string('a', 64));
            using var broker = new PausedBrokerStatus();
            await using var tester = Create();
            tester.ReuseDatabase = false;
            tester.ConfigureServices = services =>
            {
                foreach (var service in services.Where(service => service.ServiceType == typeof(IHostedService) &&
                             service.ImplementationType?.Assembly == typeof(DatabaseStartupHostedService).Assembly &&
                             service.ImplementationType != typeof(DatabaseStartupHostedService) &&
                             (!withMonitor || service.ImplementationType != typeof(BuildBrokerMonitor))).ToArray())
                    services.Remove(service);
                services.Replace(ServiceDescriptor.Singleton(new PluginBuilderOptions
                {
                    DataDir = directory,
                    BuildBrokerTokenFile = tokenPath
                }));
                services.RemoveAll<RemoteBuildSandbox>();
                services.AddSingleton(provider => new RemoteBuildSandbox(
                    provider.GetRequiredService<PluginBuilderOptions>(),
                    provider.GetRequiredService<BuildExecutorState>(),
                    NullLogger<RemoteBuildSandbox>.Instance, broker));
            };
            TaskCompletionSource hostStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            tester.ConfigureApplication = app =>
                app.Lifetime.ApplicationStarted.Register(() => hostStarted.TrySetResult());

            var starting = tester.Start();
            try
            {
                await hostStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
                if (withMonitor)
                {
                    await broker.StatusRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    // Listening on HTTP is not sufficient: the first broker status is
                    // still blocked, so Start must not hand back an unready fixture.
                    await Assert.ThrowsAsync<TimeoutException>(() => starting.WaitAsync(TimeSpan.FromMilliseconds(250)));
                }
                else
                {
                    await starting.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.False(broker.StatusRequested.Task.IsCompleted);
                }
                Assert.False(tester.GetService<BuildExecutorState>().Snapshot.IsReady);
            }
            finally
            {
                broker.AllowReady.TrySetResult();
                try { await starting.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch { /* Preserve a pending assertion; startup is observed again below. */ }
            }

            await starting.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(withMonitor, tester.GetService<BuildExecutorState>().Snapshot.IsReady);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class PausedBrokerStatus : HttpMessageHandler
    {
        public TaskCompletionSource StatusRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("/v1/status", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer " + new string('a', 64), request.Headers.Authorization?.ToString());
            StatusRequested.TrySetResult();
            await AllowReady.Task.WaitAsync(token);
            var instance = new string('b', 32);
            var image = "sha256:" + new string('c', 64);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new BrokerStatus(true, image, image, null, instance))
            };
            response.Headers.Add(RemoteBuildSandbox.InstanceHeader, instance);
            return response;
        }
    }
}
