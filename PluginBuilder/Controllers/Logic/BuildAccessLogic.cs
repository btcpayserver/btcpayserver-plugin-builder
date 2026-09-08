using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Identity;
using PluginBuilder.Services;

namespace PluginBuilder.Controllers.Logic;

public class BuildAccessLogic(
    DBConnectionFactory connectionFactory,
    UserManager<IdentityUser> userManager,
    AdminSettingsCache adminSettingsCache)
{
    public async Task<bool> CanCreateBuild(ClaimsPrincipal principal) =>
        adminSettingsCache.NewBuildsEnabled || await IsWhitelisted(principal);

    public async Task<bool> IsWhitelisted(ClaimsPrincipal principal)
    {
        var userId = userManager.GetUserId(principal);
        if (userId is null)
            return false;

        await using var conn = await connectionFactory.Open();
        return await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1
                FROM build_whitelist w
                JOIN "AspNetUsers" u ON u."Id" = w.user_id
                WHERE w.user_id = @UserId AND (NOT @RequireConfirmedEmail OR u."EmailConfirmed" = TRUE)
            )
            """, new
            {
                UserId = userId,
                RequireConfirmedEmail = adminSettingsCache.IsEmailVerificationRequiredForLogin
            });
    }
}
