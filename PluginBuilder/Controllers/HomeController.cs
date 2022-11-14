using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using PluginBuilder.APIModels;
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
        public IAuthorizationService AuthorizationService { get; }
        public ServerEnvironment Env { get; }

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
            if (HttpContext.Request.Cookies.TryGetValue(Cookies.PluginSlug, out var s) && s is not null && PluginSlug.TryParse(s, out var p))
            {
                var auth = await AuthorizationService.AuthorizeAsync(User, p, new OwnPluginRequirement());
                if (auth.Succeeded)
                    return RedirectToAction(nameof(PluginController.Dashboard), "Plugin", new { pluginSlug = p.ToString() });
                else
                    HttpContext.Response.Cookies.Delete(Cookies.PluginSlug);
            }
            return View();
        }

        [HttpGet("/logout")]
        public async Task<IActionResult> Logout()
        {
            await SignInManager.SignOutAsync();
            return RedirectToAction(nameof(Login));
        }

        [AllowAnonymous]
        [HttpGet("/api/v1/plugins/{pluginSelector}/versions/{version}/download")]
        public async Task<IActionResult> Download(
            [ModelBinder(typeof(ModelBinders.PluginSelectorModelBinder))]
            PluginSelector pluginSelector,
            [ModelBinder(typeof(ModelBinders.PluginVersionModelBinder))]
            PluginVersion version)
        {
            if (pluginSelector is null || version is null)
                return NotFound();
            var conn = await ConnectionFactory.Open();
            var slug = await conn.GetPluginSlug(pluginSelector);
            if (slug is null)
                return NotFound();
            var url = await conn.ExecuteScalarAsync<string?>(
            "SELECT b.build_info->>'url' FROM versions v " +
            "JOIN builds b ON b.plugin_slug = v.plugin_slug AND b.id = v.build_id " +
            "WHERE v.plugin_slug=@plugin_slug AND v.ver=@version",
            new
            {
                plugin_slug = slug.ToString(),
                version = version.VersionParts
            });
            if (url is null)
                return NotFound();
            return Redirect(url);
        }

        [AllowAnonymous]
        [HttpGet("/api/v1/plugins")]
        public async Task<IActionResult> Plugins(
            [ModelBinder(typeof(ModelBinders.PluginVersionModelBinder))]
            PluginVersion? btcpayVersion = null,
            bool? includePreRelease = null)
        {
            includePreRelease ??= false;
            var conn = await ConnectionFactory.Open();
            // This query probably doesn't have right indexes
            var rows = await conn.QueryAsync<(string plugin_slug, long id, string manifest_info, string build_info)>(
                "SELECT lv.plugin_slug, b.id, b.manifest_info, b.build_info FROM get_latest_versions(@btcpayVersion, @includePreRelease) lv " +
                "JOIN builds b ON b.plugin_slug = lv.plugin_slug AND b.id = lv.build_id " +
                "WHERE b.manifest_info IS NOT NULL AND b.build_info IS NOT NULL",
                new
                {
                    btcpayVersion = btcpayVersion?.VersionParts,
                    includePreRelease = includePreRelease.Value
                });
            rows.TryGetNonEnumeratedCount(out var count);
            List<PublishedVersion> versions = new List<PublishedVersion>(count);
            foreach (var r in rows)
            {
                var v = new PublishedVersion();
                v.ProjectSlug = r.plugin_slug;
                v.BuildId = r.id;
                v.BuildInfo = JObject.Parse(r.build_info);
                v.ManifestInfo = JObject.Parse(r.manifest_info);
                versions.Add(v);
            }
            return Json(versions);
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
            await conn.AddUserPlugin(pluginSlug, UserManager.GetUserId(User));
            return RedirectToAction(nameof(PluginController.Dashboard), "Plugin", new { pluginSlug = pluginSlug.ToString() });
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
