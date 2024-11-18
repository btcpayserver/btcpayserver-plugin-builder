using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using PluginBuilder.DataModels;
using PluginBuilder.Services;
using PluginBuilder.ViewModels.Account;

namespace PluginBuilder.Controllers
{
    [Authorize]
    [Route("/account/")]
    public class AccountController(
        DBConnectionFactory connectionFactory,
        UserManager<IdentityUser> userManager,
        ExternalAccountVerificationService externalAccountVerificationService,
        EmailService emailService)
        : Controller
    {
        private DBConnectionFactory ConnectionFactory { get; } = connectionFactory;
        private UserManager<IdentityUser> UserManager { get; } = userManager;

        [HttpGet("verifyemail")]
        public async Task<IActionResult> VerifyEmail()
        {
            var user = await UserManager.GetUserAsync(User);

            var emailSettings = await emailService.GetEmailSettingsFromDb();
            // TODO: Resolve the nullable issue
            var needToVerifyEmail = emailSettings?.PasswordSet == true && !await UserManager.IsEmailConfirmedAsync(user!);
            
            if (needToVerifyEmail)
            {
                var token = await UserManager.GenerateEmailConfirmationTokenAsync(user);
                var link = Url.Action("ConfirmEmail", "Home", new { uid = user.Id, token },
                    Request.Scheme, Request.Host.ToString());

                await emailService.SendVerifyEmail(user.Email, link);

                var action = nameof(HomeController.VerifyEmail);
                var ctrl = nameof(HomeController).Replace("Controller", "");
                return RedirectToAction(action, ctrl);
            }
            
            return RedirectToAction(nameof(AccountDetails));
        }

        [HttpGet("details")]
        public async Task<IActionResult> AccountDetails()
        {
            await using var conn = await ConnectionFactory.Open();
            var user = await UserManager.GetUserAsync(User);

            var emailSettings = await emailService.GetEmailSettingsFromDb();
            var needToVerifyEmail = emailSettings?.PasswordSet == true && !await UserManager.IsEmailConfirmedAsync(user!);

            var settings = await conn.GetAccountDetailSettings(user!.Id);
            var isGithubVerified = await conn.IsGithubAccountVerified(user!.Id);
            var model = new AccountDetailsViewModel
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
        public async Task<IActionResult> AccountDetails(AccountDetailsViewModel model, string? command)
        {
            await using var conn = await ConnectionFactory.Open();
            var user = await UserManager.GetUserAsync(User)!;
            var accountSettings = await conn.GetAccountDetailSettings(user!.Id);
            if (command == "VerifyGithub")
            {
                accountSettings.Github = model.Settings.Github;
                await conn.SetAccountDetailSettings(accountSettings, user!.Id);
                return RedirectToAction(nameof(GithubAccountVerification));
            }
            if (model.NeedToVerifyEmail || !model.GithubAccountVerified)
            {
                TempData[TempDataConstant.WarningMessage] = "You need to verify your Email and Github profile to update your account details";
                return View(model);
            }

            if (!ModelState.IsValid)
            {
                return View(model);
            }

            var settings = model.Settings;
            accountSettings.Nostr = model.Settings.Nostr;
            accountSettings.Twitter = model.Settings.Twitter;
            accountSettings.Email = model.Settings.Email;
            await conn.SetAccountDetailSettings(accountSettings, user!.Id);

            TempData[TempDataConstant.SuccessMessage] = "Account details updated successfully";
            return RedirectToAction(nameof(AccountDetails));
        }

        [HttpGet("verifygithubaccount")]
        public async Task<IActionResult> GithubAccountVerification()
        {
            await using var conn = await ConnectionFactory.Open();
            var user = await UserManager.GetUserAsync(User);
            var accountSettings = await conn.GetAccountDetailSettings(user!.Id);
            if (accountSettings == null || string.IsNullOrEmpty(accountSettings.Github))
            {
                return NotFound();
            }
            var isGithubVerified = await conn.IsGithubAccountVerified(user!.Id);
            return View(new VerifyGitHubViewModel { Token = user!.Id, IsVerified = isGithubVerified, GithubProfileUrl = accountSettings!.Github });
        }

        [HttpPost("verifygithubaccount")]
        public async Task<IActionResult> VerifyGithubAccount(VerifyGitHubViewModel model)
        {
            try
            {
                await using var conn = await ConnectionFactory.Open();
                var user = await UserManager.GetUserAsync(User);
                var accountSettings = await conn.GetAccountDetailSettings(user!.Id);
                var githubAccountVerification = await externalAccountVerificationService.VerifyGistToken(accountSettings.Github, model.GistUrl, user!.Id);
                if (!githubAccountVerification)
                {
                    TempData[TempDataConstant.WarningMessage] = $"Unable to verify Github profile. Kindly ensure all data is correct and try again";
                    return View(model);
                }
                await conn.VerifyGithubAccount(user!.Id, model.GistUrl);
                TempData[TempDataConstant.SuccessMessage] = $"Github account verified successfully";
                return RedirectToAction(nameof(AccountDetails));
            }
            catch (Exception ex)
            {
                TempData[TempDataConstant.WarningMessage] = $"Unable to validate Github profile: {ex.Message}";
                return View(model);
            }
        }

    }
}
