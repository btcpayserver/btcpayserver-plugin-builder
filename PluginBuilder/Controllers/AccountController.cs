using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using PluginBuilder.DataModels;
using PluginBuilder.ModelBinders;
using PluginBuilder.Services;
using PluginBuilder.ViewModels;
using PluginBuilder.ViewModels.Account;

namespace PluginBuilder.Controllers
{
    [Authorize]
    [Route("/account/")]
    public class AccountController(
        DBConnectionFactory connectionFactory,
        UserManager<IdentityUser> userManager,
        EmailService emailService)
        : Controller
    {
        private EmailService _emailService = emailService;
        private DBConnectionFactory ConnectionFactory { get; } = connectionFactory;
        private UserManager<IdentityUser> UserManager { get; } = userManager;

        [HttpGet("details")]
        public async Task<IActionResult> AccountDetails()
        {
            await using var conn = await ConnectionFactory.Open();
            var user = await UserManager.GetUserAsync(User);
            
            var emailSettings = await _emailService.GetEmailSettingsFromDb();
            var needToVerifyEmail = emailSettings.PasswordSet && !await UserManager.IsEmailConfirmedAsync(user!);
            
            var settings = await conn.GetAccountDetailSettings(user!.Id);
            var model = new AccountDetailsViewModel
            {
                AccountEmail = user.Email!,
                NeedToVerifyEmail = needToVerifyEmail,
                Settings = settings!
            };
            return View(model);
        }
        
        [HttpGet("verifyemail")]
        public async Task<IActionResult> VerifyEmail()
        {
            var user = await UserManager.GetUserAsync(User);

            var emailSettings = await _emailService.GetEmailSettingsFromDb();
            var needToVerifyEmail = emailSettings.PasswordSet && !await UserManager.IsEmailConfirmedAsync(user!);
            
            if (needToVerifyEmail)
            {
                var token = await UserManager.GenerateEmailConfirmationTokenAsync(user);
                var link = Url.Action("ConfirmEmail", "Home", new { uid = user.Id, token },
                    Request.Scheme, Request.Host.ToString());

                await _emailService.SendVerifyEmail(user.Email, link);

                var action = nameof(HomeController.VerifyEmailAddress);
                var ctrl = nameof(HomeController).Replace("Controller", "");
                return RedirectToAction(action, ctrl);
            }
            
            return RedirectToAction(nameof(AccountDetails));
        }


        [HttpPost("details")]
        public async Task<IActionResult> AccountDetails(AccountDetailsViewModel model)
        {
            if (!ModelState.IsValid)
            {
                return View(model);
            }

            await using var conn = await ConnectionFactory.Open();
            var user = await UserManager.GetUserAsync(User)!;

            var settings = model.Settings;
            var accountSettings = new AccountSettings
            {
                Nostr = settings.Nostr, Twitter = settings.Twitter, Github = settings.Github, Email = settings.Email
            };
            await conn.SetAccountDetailSettings(accountSettings, user!.Id);

            TempData[TempDataConstant.SuccessMessage] = "Account details updated successfully";
            return RedirectToAction(nameof(AccountDetails));
        }
    }
}
