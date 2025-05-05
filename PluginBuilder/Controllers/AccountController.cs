using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using PluginBuilder.DataModels;
using PluginBuilder.Extensions;
using PluginBuilder.Services;
using PluginBuilder.ViewModels.Account;

namespace PluginBuilder.Controllers;

[Authorize]
[Route("/account/")]
public class AccountController(
    PgpKeyService pgpKeyService,
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
            return RedirectToAction(action, ctrl);
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

    [HttpGet("pgpkeys")]
    public async Task<IActionResult> ManagePGPKeys()
    {
        await using var conn = await connectionFactory.Open();
        var user = await userManager.GetUserAsync(User);

        var emailSettings = await emailService.GetEmailSettingsFromDb();
        var needToVerifyEmail = emailSettings?.PasswordSet == true && !await userManager.IsEmailConfirmedAsync(user!);

        var settings = await conn.GetAccountDetailSettings(user!.Id);
        var isGithubVerified = await conn.IsGithubAccountVerified(user!.Id);
        AccountDetailsViewModel model = new()
        {
            GithubAccountVerified = isGithubVerified,
            Settings = settings!
        };
        return View(model);
    }

    [HttpGet("addpgpkeys")]
    public async Task<IActionResult> AddPGPKeys()
    {
        await using var conn = await connectionFactory.Open();
        var user = await userManager.GetUserAsync(User);

        var emailSettings = await emailService.GetEmailSettingsFromDb();
        var needToVerifyEmail = emailSettings?.PasswordSet == true && !await userManager.IsEmailConfirmedAsync(user!);
        var settings = await conn.GetAccountDetailSettings(user!.Id);
        var isGithubVerified = await conn.IsGithubAccountVerified(user!.Id);
        if (!await conn.IsGithubAccountVerified(user!.Id) || needToVerifyEmail)
        {
            TempData[TempDataConstant.WarningMessage] = "You need to verify your email and github account to proceed with add PGP";
            return View(new AddPgpKeyViewModel());
        }
        return View(new AddPgpKeyViewModel());
    }

    [HttpPost("addpgpkeys")]
    public async Task<IActionResult> AddPGPKeys(AddPgpKeyViewModel model)
    {
        try
        {
            if (!model.PublicKey.Contains("-----BEGIN PGP PUBLIC KEY BLOCK-----") ||
                !model.PublicKey.Contains("-----END PGP PUBLIC KEY BLOCK-----"))
            {
                TempData[TempDataConstant.WarningMessage] = "Invalid PGP public key format";
                return RedirectToAction(nameof(AddPGPKeys));
            }
            await using var conn = await connectionFactory.Open();
            var user = await userManager.GetUserAsync(User)!;
            var emailSettings = await emailService.GetEmailSettingsFromDb();
            var needToVerifyEmail = emailSettings?.PasswordSet == true && !await userManager.IsEmailConfirmedAsync(user!);
            var isGithubVerified = await conn.IsGithubAccountVerified(user!.Id);
            if (!await conn.IsGithubAccountVerified(user!.Id) || needToVerifyEmail)
            {
                TempData[TempDataConstant.WarningMessage] = "You need to verify your email and github account to proceed with add PGP";
                return RedirectToAction(nameof(AddPGPKeys));
            }
            var accountSettings = await conn.GetAccountDetailSettings(user!.Id) ?? new AccountSettings();
            var pgpKey = pgpKeyService.ParsePgpPublicKey(model.PublicKey, model.Title);
            if (accountSettings.PgpKey != null)
            {
                var existingFingerprints = new HashSet<string>(accountSettings.PgpKey.Select(k => k.Fingerprint));
                bool hasDuplicates = pgpKey.Any(newKey => existingFingerprints.Contains(newKey.Fingerprint));
                if (hasDuplicates)
                {
                    TempData[TempDataConstant.WarningMessage] = "Duplicate PGP key";
                    return RedirectToAction(nameof(AddPGPKeys));
                }
            }
            bool emailAddressMatch = pgpKey.Any() && pgpKey.All(k => k.PublicKeyEmailAddress.Equals(user.Email, StringComparison.OrdinalIgnoreCase));
            if (!emailAddressMatch)
            {
                TempData[TempDataConstant.WarningMessage] = "Account and PGP email address does not match";
                return RedirectToAction(nameof(AddPGPKeys));
            }
            accountSettings.PgpKey ??= new List<PgpKey>();
            accountSettings.PgpKey.AddRange(pgpKey);
            await conn.SetAccountDetailSettings(accountSettings, user!.Id);
            TempData[TempDataConstant.SuccessMessage] = "PGP key added successfully";
            return RedirectToAction(nameof(ManagePGPKeys));
        }
        catch (Exception ex)
        {
            ModelState.AddModelError(model.PublicKey, ex.Message);
            return RedirectToAction(nameof(AddPGPKeys));
        }
    }


    [HttpPost("pgpkeys/delete/{keybatchId}")]
    public async Task<IActionResult> DeletePGPKey(string keybatchId)
    {
        await using var conn = await connectionFactory.Open();
        var user = await userManager.GetUserAsync(User);

        var emailSettings = await emailService.GetEmailSettingsFromDb();
        var needToVerifyEmail = emailSettings?.PasswordSet == true && !await userManager.IsEmailConfirmedAsync(user!);

        var settings = await conn.GetAccountDetailSettings(user!.Id) ?? new AccountSettings();
        if (settings == null || settings.PgpKey == null || settings.PgpKey.FirstOrDefault(c => c.KeyBatchId == keybatchId) == null)
        {
            TempData[TempDataConstant.WarningMessage] = "No PGP key records found";
            return RedirectToAction(nameof(AddPGPKeys));
        }
        settings.PgpKey.RemoveAll(c => c.KeyBatchId == keybatchId);
        await conn.SetAccountDetailSettings(settings, user!.Id);
        TempData[TempDataConstant.SuccessMessage] = "PGP key removed successfully";
        return RedirectToAction(nameof(ManagePGPKeys));
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
