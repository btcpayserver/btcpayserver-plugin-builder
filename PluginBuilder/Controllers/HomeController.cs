using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using PluginBuilder.Services;
using PluginBuilder.ViewModels;

namespace PluginBuilder.Controllers
{
    [Authorize]
    public class HomeController : Controller
    {
        public DBConnectionFactory ConnectionFactory { get; }
        public UserManager<IdentityUser> UserManager { get; }
        public RoleManager<IdentityRole> RoleManager { get; }
        public SignInManager<IdentityUser> SignInManager { get; }
        public ServerEnvironment Env { get; }

        public HomeController(
            DBConnectionFactory connectionFactory,
            UserManager<IdentityUser> userManager,
            RoleManager<IdentityRole> roleManager,
            SignInManager<IdentityUser> signInManager,
            ServerEnvironment env)
        {
            ConnectionFactory = connectionFactory;
            UserManager = userManager;
            RoleManager = roleManager;
            SignInManager = signInManager;
            Env = env;
        }
        [HttpGet("/")]
        public IActionResult HomePage()
        {
            return View();
        }

        [AllowAnonymous]
        [HttpGet("/login")]
        public IActionResult Login()
        {
            return View(new LoginViewModel());
        }

        [AllowAnonymous]
        [HttpPost("/login")]
        public async Task<IActionResult> Login(LoginViewModel model, string? returnUrl = null)
        {
            ViewData["ReturnUrl"] = returnUrl;
            if (!ModelState.IsValid)
                return View(model);
            // Require the user to have a confirmed email before they can log on.
            var user = await UserManager.FindByEmailAsync(model.Email);
            if (user is null)
            {
                ModelState.AddModelError(string.Empty, "Invalid login attempt.");
                return View(model);
            }

            var result = await SignInManager.PasswordSignInAsync(model.Email, model.Password, model.RememberMe, lockoutOnFailure: false);
            if (!result.Succeeded)
            {
                ModelState.AddModelError(string.Empty, "Invalid login attempt.");
                return View(model);
            }
            return RedirectToLocal(returnUrl);
        }

        private IActionResult RedirectToLocal(string? returnUrl = null)
        {
            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return Redirect(returnUrl);
            }
            else
            {
                return RedirectToAction(nameof(HomeController.HomePage), "Home");
            }
        }


        [AllowAnonymous]
        [HttpGet("/register")]
        public IActionResult Register(string? returnUrl = null)
        {
            ViewData["ReturnUrl"] = returnUrl;
            return View(new RegisterViewModel());
        }

        [AllowAnonymous]
        [HttpPost("/register")]
        public async Task<IActionResult> Register(RegisterViewModel model, string? returnUrl = null)
        {
            ViewData["ReturnUrl"] = returnUrl;
            if (!ModelState.IsValid)
                return View(model);
            
            var user = new IdentityUser
            {
                UserName = model.Email,
                Email = model.Email
            };
            var result = await UserManager.CreateAsync(user);
            if (!result.Succeeded)
            {
                foreach (var error in result.Errors)
                {
                    ModelState.AddModelError("", error.Description);
                }
                return View(model);
            }

            var admins = await UserManager.GetUsersInRoleAsync(Roles.ServerAdmin);
            if (admins.Count == 0 || (model.IsAdmin && Env.CheatMode))
            {
                await UserManager.AddToRoleAsync(user, Roles.ServerAdmin);
            }

            await SignInManager.SignInAsync(user, isPersistent: false);
            return RedirectToLocal(returnUrl);
        }

        [HttpPost("/plugins/add")]
        public IActionResult AddPlugin(
            string name,
            string repository,
            string reference,
            string csprojPath)
        {

            // Wouter style: https://github.com/storefront-bvba/btcpayserver-kraken-plugin
            // Dennis style: https://github.com/dennisreimann/btcpayserver
            // Kukks sytle: https://github.com/Kukks/btcpayserver/tree/plugins/collection/Plugins
            return View();
        }
    }
}
