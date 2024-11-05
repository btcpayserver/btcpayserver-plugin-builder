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
    public class AccountController : Controller
    {
        private DBConnectionFactory ConnectionFactory { get; }
        private UserManager<IdentityUser> UserManager { get; }

        public AccountController(
            DBConnectionFactory connectionFactory,
            UserManager<IdentityUser> userManager)
        {
            ConnectionFactory = connectionFactory;
            UserManager = userManager;
        }

        [HttpGet("details")]
        public async Task<IActionResult> AccountDetails()
        {
            await using var conn = await ConnectionFactory.Open();
            var user = await UserManager.GetUserAsync(User);
            
            var settings = await conn.GetAccountDetailSettings(user!.Id);
            var model = new AccountDetailsViewModel
            {
                AccountEmail = user.Email!,
                Settings = settings!
            };
            return View(model);
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
            await conn.SetAccountDetailSettings(accountSettings, user.Id);

            TempData[TempDataConstant.SuccessMessage] = "Account details updated successfully";
            return RedirectToAction(nameof(AccountDetails));
        }
    }
}
