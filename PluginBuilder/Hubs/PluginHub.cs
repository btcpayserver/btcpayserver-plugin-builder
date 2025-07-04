using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;

namespace PluginBuilder.Hubs;

/// <summary>
///     The <see cref="HostedServices.PluginHubHostedService" /> is responsible to send messages to this hub
/// </summary>
[Authorize]
public class PluginHub : Hub
{
    private readonly IAuthorizationService _authorizationService;
    private readonly UserManager<IdentityUser> _userManager;

    public PluginHub(IAuthorizationService authorizationService, DBConnectionFactory connectionFactory,
        UserManager<IdentityUser> userManager)
    {
        ConnectionFactory = connectionFactory;
        _userManager = userManager;
        _authorizationService = authorizationService;
    }


    public DBConnectionFactory ConnectionFactory { get; }


    public override async Task OnConnectedAsync()
    {
        var user = Context.User!;
        
        var result = await _authorizationService.AuthorizeAsync(user, null, Policies.OwnPlugin);
        if (result.Succeeded && Context.GetHttpContext()?.GetPluginSlug() is { } slug)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, slug.ToString());
        }
        else
        {
            await using var connection = await ConnectionFactory.Open();
            var userId = _userManager.GetUserId(user)!;
            var plugins = await connection.GetPluginsByUserId(userId);
            foreach (var plugin in plugins) await Groups.AddToGroupAsync(Context.ConnectionId, plugin.ToString());
        }

        await base.OnConnectedAsync();
    }
}
