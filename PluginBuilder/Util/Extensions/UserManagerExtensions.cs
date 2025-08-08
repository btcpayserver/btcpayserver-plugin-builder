using Microsoft.AspNetCore.Identity;

namespace PluginBuilder.Util.Extensions;

public static class UserManagerExtensions
{
    public static async Task<List<TUser>> FindUsersByIdsAsync<TUser>(
        this UserManager<TUser> userManager,
        IEnumerable<string> userIds) where TUser : class
    {
        if (!userIds.Any())
            return new List<TUser>();

        var users = await Task.WhenAll(userIds.Select(userManager.FindByIdAsync));
        return users.Where(u => u != null).ToList();
    }
}
