using Microsoft.AspNetCore.Authorization;
using System.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Identity;
using PluginBuilder.Services;
using PluginBuilder.ViewModels;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Http;

namespace PluginBuilder.Controllers
{
    public class PasswordResetController : Controller
    {
        private UserManager<IdentityUser> UserManager { get; }
        public RoleManager<IdentityRole> RoleManager { get; }
        private ServerEnvironment Env { get; }

        public PasswordResetController(
            UserManager<IdentityUser> userManager,
            RoleManager<IdentityRole> roleManager,
            ServerEnvironment env)
        {
            UserManager = userManager;
            RoleManager = roleManager;
            Env = env;
        }

        [HttpGet("/admin/initpasswordreset")]
        public IActionResult InitPasswordReset(string adminAuthString)
        {
            if (String.IsNullOrEmpty(Env.AdminAuthString) || adminAuthString != Env.AdminAuthString)
                return Unauthorized();

            ViewData["adminAuthString"] = adminAuthString;
            return View();
        }

        [HttpPost("/admin/initpasswordreset")]
        public async Task<IActionResult> InitPasswordReset(InitPasswordResetViewModel model, string adminAuthString)
        {
            if (String.IsNullOrEmpty(Env.AdminAuthString) || adminAuthString != Env.AdminAuthString)
                return Unauthorized();


            if (!ModelState.IsValid)
                return View(model);
            // Require the user to have a confirmed email before they can log on.
            var user = await UserManager.FindByEmailAsync(model.Email);
            if (user is null)
            {
                ModelState.AddModelError(string.Empty, "User with suggested email doesn't exist");
                return View(model);
            }

            var result = await UserManager.GeneratePasswordResetTokenAsync(user);
            model.PasswordResetToken = result;
            return View(model);
        }

        // password reset flow

        [HttpGet("/passwordreset")]
        public IActionResult PasswordReset()
        {
            return View(new PasswordResetViewModel());
        }

        [HttpPost("/passwordreset")]
        public async Task<IActionResult> PasswordReset(PasswordResetViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            // Require the user to have a confirmed email before they can log on.
            var user = await UserManager.FindByEmailAsync(model.Email);
            if (user is null)
            {
                ModelState.AddModelError(string.Empty, "User with suggested email doesn't exist");
                return View(model);
            }

            var result = await UserManager.ResetPasswordAsync(user, model.PasswordResetToken, model.Password);
            model.PasswordSuccessfulyReset = result.Succeeded;

            foreach (var err in result.Errors)
                ModelState.AddModelError("PasswordResetToken", $"{err.Description}");
            
            return View(model);
        }
    }
}
