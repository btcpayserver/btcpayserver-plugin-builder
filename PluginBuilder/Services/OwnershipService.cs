using Dapper;
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

        email = email.Trim();

        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("Email cannot be empty.", nameof(email));

        var user = await userManager.FindByEmailAsync(email)
                   ?? throw new InvalidOperationException("User not found.");

        if (!await userManager.IsEmailConfirmedAsync(user))
            throw new InvalidOperationException("Owner must have a confirmed email.");

        if (!await conn.IsGithubAccountVerified(user.Id))
            throw new InvalidOperationException("Owner must have a verified Github account.");

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
        await using var tx = await conn.BeginTransactionAsync();

        var owners = (await conn.QueryAsync<(string UserId, bool IsPrimary)>(
            """
            SELECT user_id AS UserId, is_primary_owner AS IsPrimary
                      FROM users_plugins
                      WHERE plugin_slug = @slug
                      FOR UPDATE;
            """,
            new { slug = slug.ToString() }, tx)).ToList();

        var ownersCount = owners.Count;
        var primaryId   = owners.FirstOrDefault(o => o.IsPrimary).UserId;

        var currentIsPrimary = primaryId == currentUserId;
        if (!currentIsPrimary && targetUserId != currentUserId)
            throw new InvalidOperationException("Co-owners can only remove themselves.");

        if (targetUserId == primaryId)
            throw new InvalidOperationException("Cannot remove the primary owner.");

        if (ownersCount <= 1)
            throw new InvalidOperationException("Cannot remove the last owner.");

        var deleted = await conn.RemovePluginOwner(slug, targetUserId, tx);

        if (deleted != 1)
            throw new InvalidOperationException("Target owner not found.");

        await tx.CommitAsync();
    }

    public async Task LeaveAsync(PluginSlug slug, string currentUserId)
    {
        await using var conn = await connectionFactory.Open();
        await using var tx = await conn.BeginTransactionAsync();

        var owners = (await conn.QueryAsync<(string UserId, bool IsPrimary)>(
            """
            SELECT user_id AS UserId, is_primary_owner AS IsPrimary
            FROM users_plugins
            WHERE plugin_slug = @slug
            FOR UPDATE;
            """,
            new { slug = slug.ToString() }, tx)).ToList();

        if (owners.All(o => o.UserId != currentUserId))
            throw new InvalidOperationException("You are not an owner.");

        var primaryOwnerId = owners.FirstOrDefault(o => o.IsPrimary).UserId;

        if (primaryOwnerId == currentUserId)
            throw new InvalidOperationException("Transfer primary before leaving.");

        if (owners.Count <= 1)
            throw new InvalidOperationException("Cannot leave as the last owner.");

        var deleted = await conn.RemovePluginOwner(slug, currentUserId, tx);
        if (deleted != 1)
            throw new InvalidOperationException("Owner record not found.");

        await tx.CommitAsync();
    }
}
