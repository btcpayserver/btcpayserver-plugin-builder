using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Npgsql;
using PluginBuilder.Extensions;
using PluginBuilder.Services;

namespace PluginBuilder.Controllers.Logic;

public class EmailVerifiedLogic(
    UserManager<IdentityUser> userManager,
    EmailService emailService)
{
    public async Task<bool> IsUserEmailVerified(NpgsqlConnection conn, ClaimsPrincipal claimsPrincipal)
    {
        var emailSettings = await emailService.GetEmailSettingsFromDb();
        if (!await IsVerifiedEmailRequired(conn) || emailSettings?.PasswordSet != true)
            return true; // for now always return true in these cases if we don't have a verified email

        var user = await userManager.GetUserAsync(claimsPrincipal);
        return await userManager.IsEmailConfirmedAsync(user!);
    }
    
    public async Task<bool> IsVerifiedEmailRequired(NpgsqlConnection conn)
    {
        var emailVerificationRequired = await conn.GetVerifiedEmailForPluginPublishSetting();
        return emailVerificationRequired;
    }
}
