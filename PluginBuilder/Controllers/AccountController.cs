using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using PluginBuilder.DataModels;
using PluginBuilder.Services;
using PluginBuilder.Util;
using PluginBuilder.Util.Extensions;
using PluginBuilder.ViewModels;
using PluginBuilder.ViewModels.Account;

namespace PluginBuilder.Controllers;

[Authorize]
[Route("/account/")]
public class AccountController(
    GPGKeyService _gpgService,
    DBConnectionFactory connectionFactory,
    UserManager<IdentityUser> userManager,
    ExternalAccountVerificationService externalAccountVerificationService,
    EmailService emailService)
    : Controller
{
    [HttpGet("verifyemail")]
    public async Task<IActionResult> VerifyEmail()
    {
        var user = await userManager.GetUserAsync(User);
        if (user == null) throw new Exception("User not found");

        var emailSettings = await emailService.GetEmailSettingsFromDb();
        var needToVerifyEmail = emailSettings?.PasswordSet == true && !await userManager.IsEmailConfirmedAsync(user);

        if (needToVerifyEmail)
        {
            var token = await userManager.GenerateEmailConfirmationTokenAsync(user);
            var link = Url.Action("ConfirmEmail", "Home", new { uid = user.Id, token },
                Request.Scheme, Request.Host.ToString())!;

            await emailService.SendVerifyEmail(user.Email!, link);

            var action = nameof(HomeController.VerifyEmail);
            var ctrl = nameof(HomeController).Replace("Controller", "");
            return RedirectToAction(action, ctrl, new { email = user.Email! });
        }

        return RedirectToAction(nameof(AccountDetails));
    }

    [HttpGet("details")]
    public async Task<IActionResult> AccountDetails()
    {
        await using var conn = await connectionFactory.Open();
        var user = await userManager.GetUserAsync(User);

        var emailSettings = await emailService.GetEmailSettingsFromDb();
        var needToVerifyEmail = emailSettings?.PasswordSet == true && !await userManager.IsEmailConfirmedAsync(user!);

        var settings = await conn.GetAccountDetailSettings(user!.Id);
        var isGithubVerified = await conn.IsGithubAccountVerified(user!.Id);
        AccountDetailsViewModel model = new()
        {
            AccountEmail = user.Email!,
            AccountEmailConfirmed = user.EmailConfirmed,
            NeedToVerifyEmail = needToVerifyEmail,
            GithubAccountVerified = isGithubVerified,
            Settings = settings!
        };
        return View(model);
    }

    [HttpPost("details")]
    public async Task<IActionResult> AccountDetails(AccountDetailsViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var user = await userManager.GetUserAsync(User)!;

        await using var conn = await connectionFactory.Open();
        var accountSettings = await conn.GetAccountDetailSettings(user!.Id) ?? new AccountSettings();

        accountSettings.Nostr = model.Settings.Nostr;
        accountSettings.Twitter = model.Settings.Twitter;
        accountSettings.Email = model.Settings.Email;
        if (!string.IsNullOrEmpty(model.Settings.GPGKey?.PublicKey))
        {
            PgpKeyViewModel? keyViewModel = null;
            string message;
            var isPublicKeyValid = _gpgService.ValidateArmouredPublicKey(model.Settings.GPGKey.PublicKey, out message, out keyViewModel);
            if (!isPublicKeyValid)
            {
                TempData[TempDataConstant.WarningMessage] = $"GPG Key is not valid: {message}";
                return View(model);
            }
            accountSettings.GPGKey = keyViewModel!;
        }
        await conn.SetAccountDetailSettings(accountSettings, user!.Id);

        TempData[TempDataConstant.SuccessMessage] = "Account details updated successfully";
        return RedirectToAction(nameof(AccountDetails));
    }

    [HttpGet("VerifyGithubAccount")]
    public async Task<IActionResult> VerifyGithubAccount()
    {
        await using var conn = await connectionFactory.Open();
        var user = await userManager.GetUserAsync(User);
        var isGithubVerified = await conn.IsGithubAccountVerified(user!.Id);
        if (isGithubVerified)
        {
            TempData[TempDataConstant.SuccessMessage] = "GitHub account already verified";
            return RedirectToAction(nameof(AccountDetails));
        }

        var accountSettings = await conn.GetAccountDetailSettings(user!.Id) ?? new AccountSettings();
        accountSettings.Github = null;
        await conn.SetAccountDetailSettings(accountSettings, user!.Id);

        return View(new VerifyGitHubViewModel { Token = user!.Id });
    }

    [HttpPost("VerifyGithubAccount")]
    public async Task<IActionResult> VerifyGithubAccount(VerifyGitHubViewModel model)
    {
        try
        {
            var user = await userManager.GetUserAsync(User);
            var githubGistAccount = await externalAccountVerificationService.VerifyGistToken(
                model.GistUrl, user!.Id);
            if (string.IsNullOrEmpty(githubGistAccount))
            {
                TempData[TempDataConstant.WarningMessage] = "Unable to verify Github profile. Kindly ensure all data is correct and try again";
                return View(model);
            }

            await using var conn = await connectionFactory.Open();
            var accountSettings = await conn.GetAccountDetailSettings(user!.Id) ?? new AccountSettings();
            accountSettings.Github = githubGistAccount;
            await conn.SetAccountDetailSettings(accountSettings, user!.Id);

            await conn.VerifyGithubAccount(user!.Id, model.GistUrl);
            TempData[TempDataConstant.SuccessMessage] = "Github account verified successfully";
            return RedirectToAction(nameof(AccountDetails));
        }
        catch (Exception ex)
        {
            TempData[TempDataConstant.WarningMessage] = $"Unable to validate Github profile: {ex.Message}";
            return View(model);
        }
    }
}
