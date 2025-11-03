using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Npgsql;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;

namespace PluginBuilder.Controllers.Logic;

public class UserVerifiedLogic(
    UserManager<IdentityUser> userManager,
    EmailService emailService,
    UserVerifiedCache userVerifiedCache)
{
    public bool IsEmailVerificationRequiredForLogin => userVerifiedCache.IsEmailVerificationRequiredForLogin;

    public async Task<bool> IsUserEmailVerifiedForPublish(ClaimsPrincipal claimsPrincipal)
    {
        var emailSettings = await emailService.GetEmailSettingsFromDb();
        if (!userVerifiedCache.IsEmailVerificationRequiredForPublish || emailSettings?.PasswordSet != true)
            return true; // for now always return true in these cases if we don't have a verified email

        var user = await userManager.GetUserAsync(claimsPrincipal);
        return await userManager.IsEmailConfirmedAsync(user!);
    }

    public async Task<bool> IsUserEmailVerifiedForLogin(ClaimsPrincipal claimsPrincipal)
    {
        var emailSettings = await emailService.GetEmailSettingsFromDb();
        if (!userVerifiedCache.IsEmailVerificationRequiredForLogin || emailSettings?.PasswordSet != true)
            return true; // for now always return true in these cases if we don't have a verified email

        var user = await userManager.GetUserAsync(claimsPrincipal);
        return await userManager.IsEmailConfirmedAsync(user!);
    }

    public async Task<bool> IsUserGithubVerified(ClaimsPrincipal claimsPrincipal, NpgsqlConnection conn)
    {
        if (!userVerifiedCache.IsGithubVerificationRequired)
            return true;

        var user = await userManager.GetUserAsync(claimsPrincipal);
        return await conn.IsGithubAccountVerified(user!.Id);
    }

    public async Task<bool> IsNostrVerified(ClaimsPrincipal claimsPrincipal, NpgsqlConnection conn)
    {
        if (!userVerifiedCache.IsNostrVerificationRequired)
            return true;

        var user = await userManager.GetUserAsync(claimsPrincipal);
        return await conn.IsNostrAccountVerified(user!.Id);
    }
}

public class UserVerifiedCache
{
    public bool IsEmailVerificationRequiredForPublish { get; private set; }
    public bool IsEmailVerificationRequiredForLogin { get; private set; }
    public bool IsGithubVerificationRequired { get; private set; }
    public bool IsNostrVerificationRequired { get; private set; }

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

    public async Task RefreshAllUserVerifiedSettings(NpgsqlConnection conn)
    {
        await RefreshIsVerifiedEmailRequiredForPublish(conn);
        await RefreshIsVerifiedEmailRequiredForLogin(conn);
        await RefreshIsVerifiedGithubRequired(conn);
        await RefreshNostrVerified(conn);
    }

    public async Task RefreshIsVerifiedGithubRequired(NpgsqlConnection conn)
    {
        IsGithubVerificationRequired = await conn.GetVerifiedGithubSetting();
    }

    public async Task RefreshNostrVerified(NpgsqlConnection conn)
    {
        IsNostrVerificationRequired = await conn.GetVerifiedNostrSetting();
    }
}
