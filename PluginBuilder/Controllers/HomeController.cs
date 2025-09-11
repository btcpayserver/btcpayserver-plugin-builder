using System.Diagnostics;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using PluginBuilder.APIModels;
using PluginBuilder.Components.PluginVersion;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.DataModels;
using PluginBuilder.Services;
using PluginBuilder.Util;
using PluginBuilder.Util.Extensions;
using PluginBuilder.ViewModels;
using PluginBuilder.ViewModels.Home;
using PluginBuilder.ModelBinders;

namespace PluginBuilder.Controllers;

[Authorize]
public class HomeController(
    DBConnectionFactory connectionFactory,
    UserManager<IdentityUser> userManager,
    SignInManager<IdentityUser> signInManager,
    EmailService emailService,
    HttpClient httpClient,
    EmailVerifiedLogic emailVerifiedLogic,
    ServerEnvironment env)
    : Controller
{
    [AllowAnonymous]
    [HttpGet("/")]
    public IActionResult HomePage(
        [ModelBinder(typeof(PluginVersionModelBinder))] PluginVersion? btcpayVersion = null,
        string? searchPluginName = null)
    {
        return RedirectToAction(
            User.Identity?.IsAuthenticated == true
                ? nameof(Dashboard)
                : nameof(AllPlugins)
        );
    }

    // auth methods

    [HttpGet("/dashboard")]
    public async Task<IActionResult> Dashboard()
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
        var user = await userManager.FindByEmailAsync(model.Email);
        if (user is null)
        {
            ModelState.AddModelError(string.Empty, "Invalid login attempt.");
            return View(model);
        }

        var result = await signInManager.CheckPasswordSignInAsync(user, model.Password, lockoutOnFailure: true);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, "Invalid login attempt.");
            return View(model);
        }

        if (emailVerifiedLogic.IsEmailVerificationRequiredForLogin)
        {
            var principal = await signInManager.CreateUserPrincipalAsync(user);
            var isVerified = await emailVerifiedLogic.IsUserEmailVerifiedForLogin(principal);

            if (isVerified)
            {
                await signInManager.SignInAsync(user, isPersistent: model.RememberMe);
                return RedirectToLocal(returnUrl);
            }

            var token = await userManager.GenerateEmailConfirmationTokenAsync(user);
            var link = Url.Action(nameof(ConfirmEmail), "Home",
                new { uid = user.Id, token }, Request.Scheme, Request.Host.ToString())!;
            var email = user.Email!;
            await emailService.SendVerifyEmail(email, link);
            return RedirectToAction(nameof(VerifyEmail), new { email, sent = true });
        }

        await signInManager.SignInAsync(user, isPersistent: model.RememberMe);
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

            await emailService.SendVerifyEmail(model.Email, link!);

            return RedirectToAction(nameof(VerifyEmail), new { email = user.Email });
        }

        await signInManager.SignInAsync(user, false);
        return RedirectToLocal(returnUrl);
    }


    [AllowAnonymous]
    [HttpGet("public/plugins")]
    public async Task<IActionResult> AllPlugins(
        [ModelBinder(typeof(PluginVersionModelBinder))]
        PluginVersion? btcpayVersion = null, string? searchPluginName = null)
    {
        var getVersions = "get_latest_versions";
        await using var conn = await connectionFactory.Open();

        var query = $"""
                     SELECT lv.plugin_slug, lv.ver, p.settings, b.id, b.manifest_info, b.build_info
                     FROM {getVersions}(@btcpayVersion, @includePreRelease) lv
                     JOIN builds b ON b.plugin_slug = lv.plugin_slug AND b.id = lv.build_id
                     JOIN plugins p ON b.plugin_slug = p.slug
                     WHERE b.manifest_info IS NOT NULL
                     AND b.build_info IS NOT NULL
                     AND (
                         p.visibility = 'listed'
                         OR (p.visibility = 'unlisted' AND @hasSearchTerm = true)
                     )
                     AND (
                         @hasSearchTerm = false
                         OR (p.slug ILIKE @searchPattern OR b.manifest_info->>'Name' ILIKE @searchPattern)
                     )
                     ORDER BY manifest_info->>'Name'
                     """;

        var rows = await conn.QueryAsync<(string plugin_slug, int[] ver, string settings, long id, string manifest_info, string build_info)>(
            query,
            new
            {
                btcpayVersion = btcpayVersion?.VersionParts,
                includePreRelease = false,
                searchPattern = $"%{searchPluginName}%",
                hasSearchTerm = !string.IsNullOrWhiteSpace(searchPluginName)
            });

        rows.TryGetNonEnumeratedCount(out var count);
        List<PublishedPlugin> versions = new(count);
        versions.AddRange(rows.Select(r => new PublishedPlugin
        {
            ProjectSlug = r.plugin_slug,
            Version = string.Join('.', r.ver),
            BuildInfo = JObject.Parse(r.build_info),
            ManifestInfo = JObject.Parse(r.manifest_info),
            PluginLogo = JsonConvert.DeserializeObject<PluginSettings>(r.settings)!.Logo,
        }));

        return View(versions);
    }

    [AllowAnonymous]
    [HttpGet("public/plugins/{pluginSlug}")]
    public async Task<IActionResult> GetPluginDetails([ModelBinder(typeof(PluginSlugModelBinder))] PluginSlug pluginSlug)
    {
        await using var conn = await connectionFactory.Open();

        var userId = User.Identity?.IsAuthenticated == true ? userManager.GetUserId(User) : null;
        var isAdmin = User.Identity?.IsAuthenticated == true && User.IsInRole(Roles.ServerAdmin);

        var row = await conn.QueryFirstOrDefaultAsync<dynamic>(
            """
            SELECT v.plugin_slug,
                   v.ver,
                   p.settings,
                   b.manifest_info,
                   b.build_info,
                   p.visibility
            FROM versions v
            JOIN builds b ON b.plugin_slug = v.plugin_slug AND b.id = v.build_id
            JOIN plugins p ON b.plugin_slug = p.slug
            WHERE v.plugin_slug = @pluginSlug
              AND b.manifest_info IS NOT NULL
              AND b.build_info IS NOT NULL
              AND (
                    p.visibility <> 'hidden'
                    OR @isAdmin
                    OR (
                        @userId IS NOT NULL AND EXISTS (
                            SELECT 1 FROM users_plugins up
                            WHERE up.plugin_slug = v.plugin_slug AND up.user_id = @userId
                        )
                      )
                  )
            ORDER BY v.ver DESC
            LIMIT 1
            """,
            new
            {
                pluginSlug = pluginSlug.ToString(),
                userId,
                isAdmin
            });

        if (row is null)
            return NotFound();

        var settings = JsonConvert.DeserializeObject<PluginSettings>((string)row.settings)!;

        var plugin = new PublishedPlugin
        {
            ProjectSlug = pluginSlug.ToString(),
            Version = string.Join('.', row.ver),
            BuildInfo = JObject.Parse((string)row.build_info),
            ManifestInfo = JObject.Parse((string)row.manifest_info),
            PluginLogo = settings.Logo,
            Documentation = settings.Documentation
        };

        ViewBag.Contributors = await plugin.GetContributorsAsync(httpClient, plugin.pluginDir);
        ViewBag.ShowHiddenNotice = (int)row.visibility == (int)PluginVisibilityEnum.Hidden;

        return View(plugin);
    }

    [AllowAnonymous]
    [HttpGet("/VerifyEmail")]
    public IActionResult VerifyEmail(string email)
    {
        return View(model: email);
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
    public IActionResult PasswordReset(string email, string code)
    {
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(code))
        {
            // Redirect to login or show an error if parameters are missing
            TempData[TempDataConstant.WarningMessage] = "Invalid password reset link.";
            return RedirectToAction(nameof(Login));
        }

        var model = new PasswordResetViewModel { Email = email, PasswordResetToken = code };
        return View(model);
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

    [AllowAnonymous]
    [HttpGet("/forgotpassword")]
    public IActionResult ForgotPassword()
    {
        return View(new ForgotPasswordViewModel());
    }

    [AllowAnonymous]
    [HttpPost("/forgotpassword")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var user = await userManager.FindByEmailAsync(model.Email);
        // Check if user exists and if their email is confirmed before sending a reset link.
        if (user != null)
        {
            var code = await userManager.GeneratePasswordResetTokenAsync(user);
            var callbackUrl = Url.Action(nameof(PasswordReset), "Home",
                new { email = user.Email, code }, Request.Scheme);

            await emailService.SendPasswordResetLinkAsync(model.Email, callbackUrl!);
        }

        model.FormSubmitted = true;
        return View(model);
    }

    private IActionResult RedirectToLocal(string? returnUrl = null)
    {
        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)) return Redirect(returnUrl);

        return RedirectToAction(nameof(HomePage), "Home");
    }

    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View("Error", new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
