using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using PluginBuilder.Components.PluginVersion;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.Extensions;
using PluginBuilder.Services;
using PluginBuilder.ViewModels;
using PluginBuilder.ViewModels.Home;

namespace PluginBuilder.Controllers;

[Authorize]
public class HomeController(
    DBConnectionFactory connectionFactory,
    UserManager<IdentityUser> userManager,
    SignInManager<IdentityUser> signInManager,
    EmailService emailService,
    EmailVerifiedLogic emailVerifiedLogic,
    ServerEnvironment env)
    : Controller
{
    [HttpGet("/")]
    public async Task<IActionResult> HomePage()
    {
        await using var conn = await connectionFactory.Open();
        var rows =
            await conn
                .QueryAsync<(long id, string state, string? manifest_info, string? build_info, DateTimeOffset created_at, bool published, bool pre_release,
                    string slug, string? identifier)>
                (@"SELECT id, state, manifest_info, build_info, created_at, v.ver IS NOT NULL, v.pre_release, p.slug, p.identifier
FROM builds b 
    LEFT JOIN versions v ON b.plugin_slug=v.plugin_slug AND b.id=v.build_id
    JOIN plugins p ON p.slug = b.plugin_slug
    JOIN users_plugins up ON up.plugin_slug = b.plugin_slug 
WHERE up.user_id = @userId
ORDER BY created_at DESC
LIMIT 50", new { userId = userManager.GetUserId(User) });
        BuildListViewModel vm = new();
        foreach (var row in rows)
        {
            BuildListViewModel.BuildViewModel b = new();
            var buildInfo = row.build_info is null ? null : BuildInfo.Parse(row.build_info);
            var manifest = row.manifest_info is null ? null : PluginManifest.Parse(row.manifest_info);
            vm.Builds.Add(b);
            b.BuildId = row.id;
            b.State = row.state;
            b.Commit = buildInfo?.GitCommit?.Substring(0, 8);
            b.Repository = buildInfo?.GitRepository;
            b.GitRef = buildInfo?.GitRef;
            b.Version = PluginVersionViewModel.CreateOrNull(manifest?.Version?.ToString(), row.published, row.pre_release, row.state, row.slug);
            b.Date = (DateTimeOffset.UtcNow - row.created_at).ToTimeAgo();
            b.RepositoryLink = PluginController.GetUrl(buildInfo);
            b.DownloadLink = buildInfo?.Url;
            b.Error = buildInfo?.Error;
            b.PluginSlug = row.slug;
            b.PluginIdentifier = row.identifier ?? row.slug;
        }

        return View("Views/Plugin/Dashboard", vm);
    }

    // auth methods

    [HttpGet("/logout")]
    public async Task<IActionResult> Logout()
    {
        await signInManager.SignOutAsync();
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
        var user = await userManager.FindByEmailAsync(model.Email);
        if (user is null)
        {
            ModelState.AddModelError(string.Empty, "Invalid login attempt.");
            return View(model);
        }

        var result = await signInManager.PasswordSignInAsync(model.Email, model.Password, model.RememberMe, false);
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

        IdentityUser user = new() { UserName = model.Email, Email = model.Email };
        var result = await userManager.CreateAsync(user, model.Password);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors) ModelState.AddModelError("", error.Description);
            return View(model);
        }

        await using var conn = await connectionFactory.Open();

        var admins = await userManager.GetUsersInRoleAsync(Roles.ServerAdmin);
        var isAdminReg = admins.Count == 0 || (model.IsAdmin && env.CheatMode);
        if (isAdminReg) await userManager.AddToRoleAsync(user, Roles.ServerAdmin);

        // check if it's not admin and we are requiring email verifications
        var emailSettings = await emailService.GetEmailSettingsFromDb();
        if (!isAdminReg && emailSettings?.PasswordSet == true)
        {
            var token = await userManager.GenerateEmailConfirmationTokenAsync(user);
            var link = Url.Action(nameof(ConfirmEmail), "Home", new { uid = user.Id, token },
                Request.Scheme, Request.Host.ToString());

            await emailService.SendVerifyEmail(model.Email, link);

            return RedirectToAction(nameof(VerifyEmail), new { email = user.Email });
        }

        await signInManager.SignInAsync(user, false);
        return RedirectToLocal(returnUrl);
    }

    //
    [AllowAnonymous]
    [HttpGet("/VerifyEmail")]
    public IActionResult VerifyEmail(string email)
    {
        return View(email);
    }

    [AllowAnonymous]
    [HttpGet("/ConfirmEmail")]
    public async Task<IActionResult> ConfirmEmail(string uid, string token)
    {
        ConfirmEmailViewModel model = new();

        var user = await userManager.FindByIdAsync(uid);
        if (user is not null)
        {
            var result = await userManager.ConfirmEmailAsync(user, token);
            model.Email = user.Email!;
            model.EmailConfirmed = result.Succeeded;
        }

        return View(model);
    }


    [AllowAnonymous]
    [HttpGet("/UpdateEmail")]
    public async Task<IActionResult> VerifyEmailUpdate(string uid, string token)
    {
        ConfirmEmailViewModel model = new();
        await using var conn = await connectionFactory.Open();
        var user = await userManager.FindByIdAsync(uid);
        if (user is null)
            return View("ConfirmEmail", model);

        var settings = await conn.GetAccountDetailSettings(user.Id);
        if (string.IsNullOrEmpty(settings?.PendingNewEmail))
            return View("ConfirmEmail", model);

        var result = await userManager.ChangeEmailAsync(user, settings.PendingNewEmail, token);
        var setUsernameResult = await userManager.SetUserNameAsync(user, settings.PendingNewEmail);
        model.Email = settings.PendingNewEmail;
        model.EmailConfirmed = result.Succeeded && setUsernameResult.Succeeded;
        if (model.EmailConfirmed)
        {
            settings.PendingNewEmail = string.Empty;
            await conn.SetAccountDetailSettings(settings, user.Id);
        }
        return View("ConfirmEmail", model);
    }

    // password reset flow

    [AllowAnonymous]
    [HttpGet("/passwordreset")]
    public IActionResult PasswordReset()
    {
        return View(new PasswordResetViewModel());
    }

    [AllowAnonymous]
    [HttpPost("/passwordreset")]
    public async Task<IActionResult> PasswordReset(PasswordResetViewModel model)
    {
        if (!ModelState.IsValid)
            return View(model);

        // TODO: Require the user to have a confirmed email before they can log on.
        var user = await userManager.FindByEmailAsync(model.Email);
        if (user is null)
        {
            ModelState.AddModelError(string.Empty, "User with suggested email doesn't exist");
            return View(model);
        }

        var result = await userManager.ResetPasswordAsync(user, model.PasswordResetToken, model.Password);
        model.PasswordSuccessfulyReset = result.Succeeded;

        foreach (var err in result.Errors)
            ModelState.AddModelError("PasswordResetToken", $"{err.Description}");

        return View(model);
    }

    // plugin methods

    [HttpGet("/plugins/create")]
    public async Task<IActionResult> CreatePlugin()
    {
        await using var conn = await connectionFactory.Open();
        if (!await emailVerifiedLogic.IsEmailVerified(conn, User))
        {
            TempData[TempDataConstant.WarningMessage] =
                "You need to verify your email address in order to create and publish plugins";
            return RedirectToAction("AccountDetails", "Account");
        }

        return View();
    }

    [HttpPost("/plugins/create")]
    public async Task<IActionResult> CreatePlugin(CreatePluginViewModel model)
    {
        if (!PluginSlug.TryParse(model.PluginSlug, out var pluginSlug))
        {
            ModelState.AddModelError(nameof(model.PluginSlug),
                "Invalid plug slug, it should only contains latin letter in lowercase or numbers or '-' (example: my-awesome-plugin)");
            return View(model);
        }

        await using var conn = await connectionFactory.Open();
        if (!await emailVerifiedLogic.IsEmailVerified(conn, User))
        {
            TempData[TempDataConstant.WarningMessage] =
                "You need to verify your email address in order to create and publish plugins";
            return RedirectToAction("AccountDetails", "Account");
        }

        if (!await conn.NewPlugin(pluginSlug))
        {
            ModelState.AddModelError(nameof(model.PluginSlug), "This slug already exists");
            return View(model);
        }

        await conn.AddUserPlugin(pluginSlug, userManager.GetUserId(User)!);
        return RedirectToAction(nameof(PluginController.Dashboard), "Plugin", new { pluginSlug = pluginSlug.ToString() });
    }

    private IActionResult RedirectToLocal(string? returnUrl = null)
    {
        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)) return Redirect(returnUrl);

        return RedirectToAction(nameof(HomePage), "Home");
    }
}
