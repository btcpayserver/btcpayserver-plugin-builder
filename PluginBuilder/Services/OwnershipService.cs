using Microsoft.AspNetCore.Identity;
using PluginBuilder.Util.Extensions;

namespace PluginBuilder.Services;

public sealed class OwnershipService(
    DBConnectionFactory connectionFactory,
    UserManager<IdentityUser> userManager)
{
    public async Task AddOwnerByEmailAsync(PluginSlug slug, string email, string currentUserId)
    {
        await using var conn = await connectionFactory.Open();

        var primaryOwner = await conn.RetrievePluginPrimaryOwner(slug);

        if (primaryOwner != currentUserId)
            throw new InvalidOperationException("Only primary owners can add new owners.");

        var user = await userManager.FindByEmailAsync(email)
                   ?? throw new InvalidOperationException("User not found.");

        if (!await userManager.IsEmailConfirmedAsync(user))
            throw new InvalidOperationException("Owner must have an confirmed email.");

        if (!await conn.IsGithubAccountVerified(user.Id))
            throw new InvalidOperationException("Owner must have a Github account verified.");

        await conn.AddUserPlugin(slug, user.Id);
    }

    public async Task TransferPrimaryAsync(PluginSlug slug, string newPrimaryUserId, string currentUserId)
    {
        await using var conn = await connectionFactory.Open();

        var currentPrimaryId = await conn.RetrievePluginPrimaryOwner(slug);
        if (currentPrimaryId != currentUserId)
            throw new InvalidOperationException("Only the primary owner can transfer primary.");

        if (!await conn.UserOwnsPlugin(newPrimaryUserId, slug))
            throw new InvalidOperationException("Target user is not an owner.");

        await conn.AssignPluginPrimaryOwner(slug, newPrimaryUserId);
    }

    public async Task RemoveOwnerAsync(PluginSlug slug, string targetUserId, string currentUserId)
    {
        await using var conn = await connectionFactory.Open();

        var primaryId   = await conn.RetrievePluginPrimaryOwner(slug);
        var ownersCount = (await conn.RetrievePluginUserIds(slug)).Count();

        var currentIsPrimary = primaryId == currentUserId;
        if (!currentIsPrimary && targetUserId != currentUserId)
            throw new InvalidOperationException("Co-owners can only remove themselves.");

        if (targetUserId == primaryId)
            throw new InvalidOperationException("Cannot remove the primary owner.");

        if (ownersCount <= 1)
            throw new InvalidOperationException("Cannot remove the last owner.");

        await conn.RemovePluginOwner(slug, targetUserId);
    }

    public async Task LeaveAsync(PluginSlug slug, string currentUserId)
    {
        await using var conn = await connectionFactory.Open();

        var primaryOwnerId   = await conn.RetrievePluginPrimaryOwner(slug);
        var ownersCount = (await conn.RetrievePluginUserIds(slug)).Count();

        if (primaryOwnerId == currentUserId)
            throw new InvalidOperationException("Transfer primary before leaving.");

        if (ownersCount <= 1)
            throw new InvalidOperationException("Cannot leave as the last owner.");

        await conn.RemovePluginOwner(slug, currentUserId);
    }
}
