using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PluginBuilder.HostedServices;
using PluginBuilder.Util;
using Xunit;

using PluginBuilder.Builds;
using PluginBuilder.Builds.Services;

namespace PluginBuilder.Tests;

// Authorization tests control the sandbox boundary, not Docker's implementation.
internal sealed class AdmissionTestSandbox : IBuildSandbox
{
    public ConcurrentDictionary<FullBuildId, PreparedBuild> StartedPreparations { get; } = new();
    public Task? PreparationBlockedUntil { get; set; }

    public void Register(IServiceCollection services)
    {
        foreach (var service in services.Where(service => service.ServiceType == typeof(IHostedService) &&
                     service.ImplementationType == typeof(BuildBrokerMonitor)).ToArray())
            services.Remove(service);
        services.RemoveAll<IBuildSandbox>();
        services.AddSingleton<IBuildSandbox>(this);
    }

    public async Task<IPreparedBuild> PrepareAsync(
        FullBuildId buildId, BuildInfo buildInfo, CancellationToken cancellationToken = default)
    {
        var prepared = new PreparedBuild();
        Assert.True(StartedPreparations.TryAdd(buildId, prepared));
        if (PreparationBlockedUntil is { } preparation)
            await preparation.WaitAsync(cancellationToken);
        return prepared;
    }

    public async Task WaitForPreparationStartsAsync(int count)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (StartedPreparations.Count < count)
            await Task.Delay(10, deadline.Token);
    }

    internal sealed class PreparedBuild : IPreparedBuild
    {
        public bool Started { get; private set; }
        public bool Disposed { get; private set; }

        public Task<StagedBuildOutput> RunAndStageAsync(IOutputCapture buildOutput)
        {
            Started = true;
            // Stop before artifact upload; these tests assert authorization only.
            return Task.FromException<StagedBuildOutput>(new BuildServiceException("Plugin build failed."));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
