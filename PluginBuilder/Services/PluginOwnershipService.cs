using Microsoft.AspNetCore.Identity;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.Util.Extensions;
using PluginBuilder.ViewModels;

namespace PluginBuilder.Services;

public sealed class PluginOwnershipService(
    DBConnectionFactory connectionFactory,
    UserManager<IdentityUser> userManager,
    IUserClaimsPrincipalFactory<IdentityUser> principalFactory,
    UserVerifiedLogic userVerifiedLogic)
{
    public async Task<ServiceResult> AddOwnerByEmailAsync(PluginSlug pluginSlug, string email)
    {
        email = (email ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(email))
            return ServiceResult.Fail("Email is required.");

        var user = await userManager.FindByEmailAsync(email);
        if (user is null)
            return ServiceResult.Fail("User not found.");

        await using var conn = await connectionFactory.Open();

        if (await conn.UserOwnsPlugin(user.Id, pluginSlug))
            return ServiceResult.Fail("User is already an owner.");

        var principal = await principalFactory.CreateAsync(user);

        if (!await userVerifiedLogic.IsUserEmailVerifiedForPublish(principal))
            return ServiceResult.Fail("Owner must have a confirmed email.");

        if (!await userVerifiedLogic.IsUserGithubVerified(principal, conn))
            return ServiceResult.Fail("Owner must have a verified Github account.");

        await conn.AddUserPlugin(pluginSlug, user.Id, false);
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> TransferPrimaryAsync(PluginSlug pluginSlug, string targetUserId)
    {
        await using var conn = await connectionFactory.Open();

        if (!await conn.UserOwnsPlugin(targetUserId, pluginSlug))
            return ServiceResult.Fail("Target user is not an owner.");

        var ok = await conn.AssignPluginPrimaryOwner(pluginSlug, targetUserId);
        return ok ? ServiceResult.Ok() : ServiceResult.Fail("Failed to assign primary owner.");
    }

    public async Task<ServiceResult> RemoveOwnerAsync(
        PluginSlug pluginSlug,
        string targetUserId,
        string? currentUserId,
        bool isServerAdmin)
    {
        await using var conn = await connectionFactory.Open();
        await using var tx = await conn.BeginTransactionAsync();

        var owners = await conn.GetPluginOwnersForUpdate(pluginSlug, tx);

        if (owners.All(o => o.UserId != targetUserId))
            return ServiceResult.Fail("User not an owner.");

        var primaryOwnerId = owners.FirstOrDefault(o => o.IsPrimary).UserId;

        if (!isServerAdmin)
        {
            if (currentUserId is null)
                return ServiceResult.Fail("Not authenticated.");

            var currentIsPrimary = primaryOwnerId == currentUserId;
            if (!currentIsPrimary && targetUserId != currentUserId)
                return ServiceResult.Fail("Only primary owner can remove other owners.");
        }

        if (targetUserId == primaryOwnerId)
            return ServiceResult.Fail("Primary owner cannot be removed. Transfer primary ownership first.");

        if (owners.Count <= 1)
            return ServiceResult.Fail("Cannot remove the last owner.");

        var deleted = await conn.RemovePluginOwner(pluginSlug, targetUserId);
        if (deleted != 1)
            return ServiceResult.Fail("Failed to remove owner.");

        await tx.CommitAsync();
        return ServiceResult.Ok();
    }
}
