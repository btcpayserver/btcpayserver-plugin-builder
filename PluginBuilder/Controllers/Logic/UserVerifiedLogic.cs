using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Npgsql;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;

namespace PluginBuilder.Controllers.Logic;

public class UserVerifiedLogic(
    UserManager<IdentityUser> userManager,
    EmailService emailService,
    AdminSettingsCache adminSettingsCache)
{
    public bool IsEmailVerificationRequiredForLogin => adminSettingsCache.IsEmailVerificationRequiredForLogin;

    public async Task<bool> IsUserEmailVerifiedForPublish(ClaimsPrincipal claimsPrincipal)
    {
        var emailSettings = await emailService.GetEmailSettingsFromDb();
        if (!adminSettingsCache.IsEmailVerificationRequiredForPublish || emailSettings?.PasswordSet != true)
            return true; // for now always return true in these cases if we don't have a verified email

        var user = await userManager.GetUserAsync(claimsPrincipal);
        return await userManager.IsEmailConfirmedAsync(user!);
    }

    public async Task<bool> IsUserEmailVerifiedForLogin(ClaimsPrincipal claimsPrincipal)
    {
        var emailSettings = await emailService.GetEmailSettingsFromDb();
        if (!adminSettingsCache.IsEmailVerificationRequiredForLogin || emailSettings?.PasswordSet != true)
            return true; // for now always return true in these cases if we don't have a verified email

        var user = await userManager.GetUserAsync(claimsPrincipal);
        return await userManager.IsEmailConfirmedAsync(user!);
    }

    public async Task<bool> IsUserGithubVerified(ClaimsPrincipal claimsPrincipal, NpgsqlConnection conn)
    {
        if (!adminSettingsCache.IsGithubVerificationRequired)
            return true;

        var user = await userManager.GetUserAsync(claimsPrincipal);
        return await conn.IsGithubAccountVerified(user!.Id);
    }

    public async Task<bool> IsNostrVerified(ClaimsPrincipal claimsPrincipal, NpgsqlConnection conn)
    {
        if (!adminSettingsCache.IsNostrVerificationRequired)
            return true;

        var user = await userManager.GetUserAsync(claimsPrincipal) ?? throw new Exception("User not found");
        var settings = await conn.GetAccountDetailSettings(user.Id);
        return settings?.Nostr is { Npub: not null, Proof: not null };
    }
}
