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
    public bool IsEmailVerificationRequiredForLogin => emailVerifiedCache.IsEmailVerificationRequiredForLogin;

    public async Task<bool> IsUserEmailVerifiedForPublish(ClaimsPrincipal claimsPrincipal)
    {
        var emailSettings = await emailService.GetEmailSettingsFromDb();
        if (!emailVerifiedCache.IsEmailVerificationRequiredForPublish || emailSettings?.PasswordSet != true)
            return true; // for now always return true in these cases if we don't have a verified email

        var user = await userManager.GetUserAsync(claimsPrincipal);
        return await userManager.IsEmailConfirmedAsync(user!);
    }

    public async Task<bool> IsUserEmailVerifiedForLogin(ClaimsPrincipal claimsPrincipal)
    {
        var emailSettings = await emailService.GetEmailSettingsFromDb();
        if (!emailVerifiedCache.IsEmailVerificationRequiredForLogin || emailSettings?.PasswordSet != true)
            return true; // for now always return true in these cases if we don't have a verified email

        var user = await userManager.GetUserAsync(claimsPrincipal);
        return await userManager.IsEmailConfirmedAsync(user!);
    }
}

public class EmailVerifiedCache
{
    public bool IsEmailVerificationRequiredForPublish { get; private set; }
    public bool IsEmailVerificationRequiredForLogin { get; private set; }

    public async Task RefreshIsVerifiedEmailRequiredForPublish(NpgsqlConnection conn)
    {
        IsEmailVerificationRequiredForPublish = await conn.GetVerifiedEmailForPluginPublishSetting();
    }

    public async Task RefreshIsVerifiedEmailRequiredForLogin(NpgsqlConnection conn)
    {
        IsEmailVerificationRequiredForLogin = await conn.GetVerifiedEmailForLoginSetting();
    }

    public async Task RefreshAllVerifiedEmailSettings(NpgsqlConnection conn)
    {
        await RefreshIsVerifiedEmailRequiredForPublish(conn);
        await RefreshIsVerifiedEmailRequiredForLogin(conn);
    }
}
