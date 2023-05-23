using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using PluginBuilder.Events;
using PluginBuilder.Services;

namespace PluginBuilder.Hubs
{
    /// <summary>
    /// The <see cref="HostedServices.PluginHubHostedService"/> is responsible to send messages to this hub
    /// </summary>
    [Authorize(Policy = Policies.OwnPlugin)]
    public class PluginHub : Hub
    {
        public PluginSlug PluginSlug
        {
            get
            {
                return Context.Items["PLUGIN_SLUG"] as PluginSlug ?? throw new InvalidOperationException("The plugin slug isn't affected");
            }
            set
            {
                Context.Items["PLUGIN_SLUG"] = value;
            }
        }

        public override async Task OnConnectedAsync()
        {
            PluginSlug = Context.GetHttpContext()?.GetPluginSlug() ?? throw new InvalidOperationException("No plugin slug affected to connection");
            await Groups.AddToGroupAsync(Context.ConnectionId, PluginSlug.ToString());
            await base.OnConnectedAsync();
        }
    }
}
