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
    public bool IsEmailVerificationRequired { get; private set; }
    
    public async Task<bool> IsUserEmailVerified(ClaimsPrincipal claimsPrincipal)
    {
        var emailSettings = await emailService.GetEmailSettingsFromDb();
        if (!IsEmailVerificationRequired || emailSettings?.PasswordSet != true)
            return true; // for now always return true in these cases if we don't have a verified email

        var user = await userManager.GetUserAsync(claimsPrincipal);
        return await userManager.IsEmailConfirmedAsync(user!);
    }
    
    public async Task<bool> RefreshIsVerifiedEmailRequired(NpgsqlConnection conn)
    {
        IsEmailVerificationRequired = await conn.GetVerifiedEmailForPluginPublishSetting();
        return IsEmailVerificationRequired;
    }
}
