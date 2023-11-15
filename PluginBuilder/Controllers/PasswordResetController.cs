using Microsoft.AspNetCore.Authorization;
using System.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Identity;
using PluginBuilder.Services;
using PluginBuilder.ViewModels;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace PluginBuilder.Controllers
{
    public class PasswordResetController : Controller
    {
        private DBConnectionFactory ConnectionFactory { get; }
        private UserManager<IdentityUser> UserManager { get; }
        public RoleManager<IdentityRole> RoleManager { get; }
        private SignInManager<IdentityUser> SignInManager { get; }
        private IAuthorizationService AuthorizationService { get; }
        private ServerEnvironment Env { get; }

        public PasswordResetController(
            DBConnectionFactory connectionFactory,
            UserManager<IdentityUser> userManager,
            RoleManager<IdentityRole> roleManager,
            SignInManager<IdentityUser> signInManager,
            IAuthorizationService authorizationService,
            ServerEnvironment env)
        {
            ConnectionFactory = connectionFactory;
            UserManager = userManager;
            RoleManager = roleManager;
            SignInManager = signInManager;
            AuthorizationService = authorizationService;
            Env = env;
        }

        [HttpGet("/admin/initpasswordreset")]
        public IActionResult InitPasswordReset()
        {
            return View();
        }
        public IActionResult Children()
        {
            return View();
        }

        [HttpPost("/admin/initpasswordreset")]
        public async Task<IActionResult> InitPasswordReset(InitPasswordResetViewModel model)
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

            var result = await UserManager.GeneratePasswordResetTokenAsync(user);
            model.PasswordResetToken = result;
            return View(model);
        }

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
