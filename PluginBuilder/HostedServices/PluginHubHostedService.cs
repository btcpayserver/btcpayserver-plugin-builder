using Microsoft.AspNetCore.SignalR;
using PluginBuilder.Events;
using PluginBuilder.Hubs;
using PluginBuilder.Services;

namespace PluginBuilder.HostedServices;

public class PluginHubHostedService : IHostedService
{
    private readonly List<IDisposable> _disposables = new();

    public PluginHubHostedService(IHubContext<PluginHub> pluginHub, EventAggregator eventAggregator)
    {
        PluginHub = pluginHub;
        EventAggregator = eventAggregator;
    }

    private IHubContext<PluginHub> PluginHub { get; }
    private EventAggregator EventAggregator { get; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _disposables.Add(EventAggregator.SubscribeAsync<BuildChanged>(async ev =>
        {
            var fullBuildId = ev.FullBuildId.ToString();
            var pluginSlug = ev.FullBuildId.PluginSlug.ToString();
            var args = new
            {
                fullBuildId,
                pluginSlug,
                ev.EventName,
                ev.BuildInfo,
                ev.ManifestInfo
            };
            await PluginHub.Clients.Group(pluginSlug).SendAsync("build-changed", args, cancellationToken);
        }));
        _disposables.Add(EventAggregator.SubscribeAsync<BuildLogUpdated>(async ev =>
        {
            var fullBuildId = ev.FullBuildId.ToString();
            var pluginSlug = ev.FullBuildId.PluginSlug.ToString();
            var args = new
            {
                fullBuildId,
                pluginSlug,
                ev.Log
            };
            await PluginHub.Clients.Group(pluginSlug).SendAsync("build-log-updated", args, cancellationToken);
        }));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var d in _disposables)
            d.Dispose();
        return Task.CompletedTask;
    }
}
