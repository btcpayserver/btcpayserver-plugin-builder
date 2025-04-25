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
    public async Task<bool> IsEmailVerified(NpgsqlConnection conn, ClaimsPrincipal claimsPrincipal)
    {
        var emailVerificationRequired = await conn.GetVerifiedEmailForPluginPublishSetting();
        var emailSettings = await emailService.GetEmailSettingsFromDb();
        if (!emailVerificationRequired || emailSettings?.PasswordSet != true)
            return true; // for now always return true in these cases if we don't have a verified email

        var user = await userManager.GetUserAsync(claimsPrincipal);
        return await userManager.IsEmailConfirmedAsync(user!);
    }
}
