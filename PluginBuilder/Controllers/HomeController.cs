using Dapper;
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
        private DBConnectionFactory ConnectionFactory { get; }
        private UserManager<IdentityUser> UserManager { get; }
        public RoleManager<IdentityRole> RoleManager { get; }
        private SignInManager<IdentityUser> SignInManager { get; }
        private IAuthorizationService AuthorizationService { get; }
        private ServerEnvironment Env { get; }

        public HomeController(
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

        [HttpGet("/")]
        
        public async Task<IActionResult> HomePage()
        {
            await using var conn = await ConnectionFactory.Open();
            var rows = await conn.QueryAsync<(long id, string state, string? manifest_info, string? build_info, DateTimeOffset created_at, bool published, bool pre_release, string slug, string? identifier)>
            (@"SELECT id, state, manifest_info, build_info, created_at, v.ver IS NOT NULL, v.pre_release, p.slug, p.identifier
FROM builds b 
    LEFT JOIN versions v ON b.plugin_slug=v.plugin_slug AND b.id=v.build_id
    JOIN plugins p ON p.slug = b.plugin_slug
    JOIN users_plugins up ON up.plugin_slug = b.plugin_slug 
WHERE up.user_id = @userId
ORDER BY created_at DESC
LIMIT 50", new { userId = UserManager.GetUserId(User) });
            var vm = new BuildListViewModel();
            foreach (var row in rows)
            {
                var b = new BuildListViewModel.BuildViewModel();
                var buildInfo = row.build_info is null ? null : BuildInfo.Parse(row.build_info);
                var manifest = row.manifest_info is null ? null : PluginManifest.Parse(row.manifest_info);
                vm.Builds.Add(b);
                b.BuildId = row.id;
                b.State = row.state;
                b.Commit = buildInfo?.GitCommit?.Substring(0, 8);
                b.Repository = buildInfo?.GitRepository;
                b.GitRef = buildInfo?.GitRef;
                b.Version = Components.PluginVersion.PluginVersionViewModel.CreateOrNull(manifest?.Version?.ToString(), row.published, row.pre_release, row.slug);
                b.Date = (DateTimeOffset.UtcNow - row.created_at).ToTimeAgo();
                b.RepositoryLink = PluginController.GetUrl(buildInfo);
                b.DownloadLink = buildInfo?.Url;
                b.Error = buildInfo?.Error;
                b.PluginSlug = row.slug;
                b.PluginIdentifier = row.identifier ?? row.slug;
            }
            return View("Views/Plugin/Dashboard",vm);
        }

        [HttpGet("/logout")]
        public async Task<IActionResult> Logout()
        {
            await SignInManager.SignOutAsync();
            return RedirectToAction(nameof(Login));
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
                Email = model.Email,
            };
            var result = await UserManager.CreateAsync(user, model.Password);
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

        [HttpGet("/plugins/create")]
        public IActionResult CreatePlugin()
        {
            return View();
        }

        [HttpPost("/plugins/create")]
        public async Task<IActionResult> CreatePlugin(CreatePluginViewModel model)
        {
            if (!PluginSlug.TryParse(model.PluginSlug, out var pluginSlug))
            {
                ModelState.AddModelError(nameof(model.PluginSlug), "Invalid plug slug, it should only contains latin letter in lowercase or numbers or '-' (example: my-awesome-plugin)");
                return View(model);
            }
            using var conn = await ConnectionFactory.Open();
            if (!await conn.NewPlugin(pluginSlug))
            {
                ModelState.AddModelError(nameof(model.PluginSlug), "This slug already exists");
                return View(model);
            }
            await conn.AddUserPlugin(pluginSlug, UserManager.GetUserId(User)!);
            return RedirectToAction(nameof(PluginController.Dashboard), "Plugin", new { pluginSlug = pluginSlug.ToString() });
        }

        private IActionResult RedirectToLocal(string? returnUrl = null)
        {
            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return Redirect(returnUrl);
            }
            else
            {
                return RedirectToAction(nameof(HomePage), "Home");
            }
        }
    }
}
