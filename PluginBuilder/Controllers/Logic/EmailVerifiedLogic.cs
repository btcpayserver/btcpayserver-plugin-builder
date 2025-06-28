using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Npgsql;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;

namespace PluginBuilder.Controllers.Logic;

public class EmailVerifiedLogic(
    UserManager<IdentityUser> userManager,
    EmailService emailService,
    EmailVerifiedCache emailVerifiedCache)
{
    public bool IsEmailVerificationRequired => emailVerifiedCache.IsEmailVerificationRequired;

    public async Task<bool> IsUserEmailVerified(ClaimsPrincipal claimsPrincipal)
    {
        var emailSettings = await emailService.GetEmailSettingsFromDb();
        if (!emailVerifiedCache.IsEmailVerificationRequired || emailSettings?.PasswordSet != true)
            return true; // for now always return true in these cases if we don't have a verified email

        var user = await userManager.GetUserAsync(claimsPrincipal);
        return await userManager.IsEmailConfirmedAsync(user!);
    }
}

public class EmailVerifiedCache
{
    public bool IsEmailVerificationRequired { get; private set; }

    public async Task<bool> RefreshIsVerifiedEmailRequired(NpgsqlConnection conn)
    {
        IsEmailVerificationRequired = await conn.GetVerifiedEmailForPluginPublishSetting();
        return IsEmailVerificationRequired;
    }
}
