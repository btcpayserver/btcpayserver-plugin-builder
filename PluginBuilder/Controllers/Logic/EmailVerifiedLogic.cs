using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Npgsql;
using PluginBuilder.Services;

namespace PluginBuilder.Controllers.Logic;

public class EmailVerifiedLogic(
    UserManager<IdentityUser> userManager,
    EmailService emailService)
{
    public async Task<bool> IsEmailVerified(NpgsqlConnection conn, ClaimsPrincipal User)
    {
        var emailVerificationRequired = await conn.GetVerifiedEmailForPluginPublishSetting();
        var emailSettings = await emailService.GetEmailSettingsFromDb();
        if (emailVerificationRequired && emailSettings?.PasswordSet == true)
        {
            var user = await userManager.GetUserAsync(User);
            return await userManager.IsEmailConfirmedAsync(user!);
        }

        return true; // for now always return true in these cases if we don't have a verified email
    }
}
