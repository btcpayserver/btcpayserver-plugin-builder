using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace PluginBuilder.Util.Extensions;

public static class UserManagerExtensions
{
    public static async Task<List<TUser>> FindUsersByIdsAsync<TUser>(
        this UserManager<TUser> userManager,
        IEnumerable<string> userIds) where TUser : class
    {
        var ids = userIds.Distinct().ToArray();
        if (ids.Length == 0)
            return new List<TUser>();

        return await userManager.Users
            .Where(u => ids.Contains(EF.Property<string>(u, "Id")))
            .AsNoTracking()
            .ToListAsync();
    }
}
