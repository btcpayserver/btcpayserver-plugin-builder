using Microsoft.AspNetCore.SignalR;
using PluginBuilder.Events;
using PluginBuilder.Hubs;
using PluginBuilder.Services;

namespace PluginBuilder.HostedServices
{
    public class PluginHubHostedService : IHostedService
    {
        public PluginHubHostedService(IHubContext<Hubs.PluginHub> pluginHub, EventAggregator eventAggregator)
        {
            PluginHub = pluginHub;
            EventAggregator = eventAggregator;
        }

        public IHubContext<PluginHub> PluginHub { get; }
        public EventAggregator EventAggregator { get; }

        List<IDisposable> _disposables = new List<IDisposable>();
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _disposables.Add(EventAggregator.SubscribeAsync<BuildChanged>(async o =>
            {
                await PluginHub.Clients.Group(o.FullBuildId.PluginSlug.ToString()).SendAsync("BuildUpdated");
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
}
