using System.Diagnostics;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
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
using PluginBuilder.JsonConverters;

namespace PluginBuilder.Controllers;

[Authorize]
public class HomeController(
    DBConnectionFactory connectionFactory,
    UserManager<IdentityUser> userManager,
    SignInManager<IdentityUser> signInManager,
    EmailService emailService,
    HttpClient httpClient,
    UserVerifiedLogic userVerifiedLogic,
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

        if (userVerifiedLogic.IsEmailVerificationRequiredForLogin)
        {
            var principal = await signInManager.CreateUserPrincipalAsync(user);
            var isVerified = await userVerifiedLogic.IsUserEmailVerifiedForLogin(principal);

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
            ViewData["VerifyEmailTitle"] = "Email confirmation required to sign in";
            ViewData["VerifyEmailDescription"] =
                "After you confirm your email, please sign in again to continue.";
            return View(nameof(VerifyEmail), model: email);
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
                     WITH review_stats AS (
                       SELECT
                         plugin_slug,
                         AVG(rating) AS avg_rating,
                         COUNT(*)    AS total_reviews
                       FROM plugin_reviews
                       GROUP BY plugin_slug
                     )
                     SELECT
                       lv.plugin_slug,
                       lv.ver,
                       p.settings,
                       b.id,
                       b.manifest_info,
                       b.build_info,
                       COALESCE(rs.avg_rating, 0.0) AS avg_rating,
                       COALESCE(rs.total_reviews, 0) AS total_reviews
                     FROM {getVersions}(@btcpayVersion, @includePreRelease) lv
                     JOIN builds  b ON b.plugin_slug = lv.plugin_slug AND b.id = lv.build_id
                     JOIN plugins p ON b.plugin_slug = p.slug
                     LEFT JOIN review_stats rs ON rs.plugin_slug = lv.plugin_slug
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
                     ORDER BY b.manifest_info->>'Name'
                     """;

        var rows = await conn.QueryAsync<(string plugin_slug, int[] ver, string settings, long id, string manifest_info, string build_info, decimal avg_rating, int total_reviews)>(
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
        versions.AddRange(rows.Select(r =>
        {
            var manifestInfo = JObject.Parse(r.manifest_info);
            PluginSettings? settings = SafeJson.Deserialize<PluginSettings>(r.settings);
            return new PublishedPlugin
            {
                PluginTitle = settings?.PluginTitle ?? manifestInfo["Name"]?.ToString(),
                Description = settings?.Description ?? manifestInfo["Description"]?.ToString(),
                ProjectSlug = r.plugin_slug,
                Version = string.Join('.', r.ver),
                BuildInfo = JObject.Parse(r.build_info),
                ManifestInfo = manifestInfo,
                PluginLogo = settings?.Logo,
                RatingSummary = new PluginRatingSummary
                {
                    Average = r.avg_rating,
                    TotalReviews = r.total_reviews
                }
            };
        }));
        return View(versions);
    }

    [AllowAnonymous]
    [HttpGet("public/plugins/{pluginSlug}")]
    public async Task<IActionResult> GetPluginDetails(
        [ModelBinder(typeof(PluginSlugModelBinder))]
        PluginSlug pluginSlug,
        [FromQuery] PluginDetailsViewModel? model)
    {
        model ??= new PluginDetailsViewModel();

        var sort = string.Equals(model.Sort, "helpful", StringComparison.OrdinalIgnoreCase) ? "helpful" : "newest";

        if (model.RatingFilter is < 1 or > 5) model.RatingFilter = null;

        var userId = User.Identity?.IsAuthenticated == true ? userManager.GetUserId(User) : null;
        var isAdmin = User.Identity?.IsAuthenticated == true && User.IsInRole(Roles.ServerAdmin);

        var orderBy = sort == "helpful"
            ? " (hv.up_count - hv.down_count) DESC, r.created_at DESC "
            : " r.created_at DESC ";

        var prms = new
        {
            pluginSlug = pluginSlug.ToString(),
            currentUserId = userId,
            isAdmin,
            skip = model.Skip,
            take = model.Count,
            sort,
            rating = model.RatingFilter
        };

         var sql =
                         @"
                         -- FIRST QUERY
                         SELECT
                           v.plugin_slug,
                           array_to_string(v.ver, '.') AS ver_str,
                           p.settings,
                           b.manifest_info,
                           b.build_info,
                           p.visibility,
                           (SELECT b2.created_at
                              FROM builds b2
                             WHERE b2.plugin_slug = v.plugin_slug
                             ORDER BY b2.id ASC
                             LIMIT 1) AS created_at,
                           (
                             SELECT array_agg(array_to_string(ver, '.') ORDER BY ver DESC)
                             FROM versions
                             WHERE plugin_slug = v.plugin_slug
                           ) AS versions
                         FROM versions v
                         JOIN builds  b ON b.plugin_slug = v.plugin_slug AND b.id = v.build_id
                         JOIN plugins p ON b.plugin_slug = p.slug
                         WHERE v.plugin_slug = @pluginSlug
                           AND b.manifest_info IS NOT NULL
                           AND b.build_info  IS NOT NULL
                           AND (
                                 p.visibility <> 'hidden'
                                 OR @isAdmin
                                 OR (
                                     @currentUserId IS NOT NULL AND EXISTS (
                                         SELECT 1 FROM users_plugins up
                                         WHERE up.plugin_slug = v.plugin_slug AND up.user_id = @currentUserId
                                     )
                                 )
                               )
                         ORDER BY v.ver DESC
                         LIMIT 1;

                        -- SECOND QUERY
                        SELECT
                          COALESCE(AVG(rating), 0) AS ""Average"",
                          COUNT(*)                            AS ""TotalReviews"",
                          COUNT(*) FILTER (WHERE rating = 1)  AS ""C1"",
                          COUNT(*) FILTER (WHERE rating = 2)  AS ""C2"",
                          COUNT(*) FILTER (WHERE rating = 3)  AS ""C3"",
                          COUNT(*) FILTER (WHERE rating = 4)  AS ""C4"",
                          COUNT(*) FILTER (WHERE rating = 5)  AS ""C5""
                        FROM plugin_reviews
                        WHERE plugin_slug = @pluginSlug;

                        -- THIRD QUERY
                        SELECT
                          r.id AS Id,
                          (u.""AccountDetail""->>'github') AS ""AuthorUrl"",
                          r.rating AS Rating,
                          r.body AS Body,
                          array_to_string(r.plugin_version, '.')::text AS ""PluginVersion"",
                          r.created_at AS ""CreatedAt"",
                          COALESCE(hv.up_count, 0)   AS ""UpCount"",
                          COALESCE(hv.down_count, 0) AS ""DownCount"",
                          ( @currentUserId IS NOT NULL AND r.user_id = @currentUserId ) AS ""IsReviewOwner"",
                          CASE
                            WHEN @currentUserId IS NULL THEN NULL
                            WHEN r.helpful_voters ? @currentUserId
                              THEN (r.helpful_voters ->> @currentUserId)::boolean
                            ELSE NULL
                          END AS ""UserVoteHelpful""
                        FROM plugin_reviews r
                        LEFT JOIN ""AspNetUsers"" u ON u.""Id"" = r.user_id
                        LEFT JOIN LATERAL (
                          SELECT
                            COUNT(*) FILTER (WHERE kv.value::boolean)      AS up_count,
                            COUNT(*) FILTER (WHERE NOT kv.value::boolean)  AS down_count
                          FROM jsonb_each_text(COALESCE(r.helpful_voters, '{}'::jsonb)) kv
                        ) hv ON TRUE
                        WHERE r.plugin_slug = @pluginSlug AND (@rating IS NULL OR r.rating = @rating)
                        ORDER BY " + orderBy + @"
                        OFFSET @skip LIMIT @take;"
                         ;

        await using var conn = await connectionFactory.Open();
        await using var multi = await conn.QueryMultipleAsync(sql, prms);

        //first
        var pluginDetails = await multi.ReadFirstOrDefaultAsync<dynamic>();
        if (pluginDetails is null) return NotFound();
        var versions = pluginDetails.versions as IEnumerable<string> ?? Enumerable.Empty<string>();

        //second
        var summary = await multi.ReadFirstOrDefaultAsync<PluginRatingSummary>()
                      ?? new PluginRatingSummary();

        // third
        var items = (await multi.ReadAsync<Review>()).ToList();

        foreach (var item in items)
        {
            var gh = GetGithubIdentity(item.AuthorUrl, size: 48);
            if (gh is null)
            {
                item.AuthorDisplay   = "Anonymous";
            }
            else
            {
                item.AuthorDisplay   = gh.Login;
                item.AuthorUrl       = gh.HtmlUrl;
                item.AuthorAvatarUrl = gh.AvatarUrl;
            }
        }
        var settings = SafeJson.Deserialize<PluginSettings>((string)pluginDetails.settings);
        var manifestInfo = JObject.Parse((string)pluginDetails.manifest_info);
        var plugin = new PublishedPlugin
        {
            PluginTitle = settings?.PluginTitle ?? manifestInfo["Name"]?.ToString(),
            Description = settings?.Description ?? manifestInfo["Description"]?.ToString(),
            ProjectSlug = pluginSlug.ToString(),
            ManifestInfo = manifestInfo,
            PluginLogo = settings?.Logo,
            Documentation = settings?.Documentation,
            Version       = (string)pluginDetails.ver_str,
            BuildInfo     = JObject.Parse((string)pluginDetails.build_info),
            CreatedDate   = (DateTimeOffset)pluginDetails.created_at,
            RatingSummary = summary
        };

        var isOwner = false;
            if (userId != null)
                isOwner = await conn.UserOwnsPlugin(userId, pluginSlug);

        var vm = new PluginDetailsViewModel
        {
            Plugin = plugin,
            Sort = sort,
            Skip = model.Skip,
            Reviews = items,
            IsAdmin = isAdmin,
            IsOwner = isOwner,
            PluginVersions = versions.ToList(),
            ShowHiddenNotice = (int)pluginDetails.visibility == (int)PluginVisibilityEnum.Hidden,
            Contributors = await plugin.GetContributorsAsync(httpClient, plugin.pluginDir),
            RatingFilter = model.RatingFilter
        };

        return View(vm);
    }

    [HttpPost("public/plugins/{pluginSlug}/reviews/upsert")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpsertReview(
        [ModelBinder(typeof(PluginSlugModelBinder))] PluginSlug pluginSlug,
        int rating,
        string? body,
        string? pluginVersion)
    {
        if (rating is < 1 or > 5) return BadRequest("Invalid rating.");

        var userId = userManager.GetUserId(User);
        if (string.IsNullOrEmpty(userId)) return Forbid();

        await using var conn = await connectionFactory.Open();

        var isOwner = await conn.UserOwnsPlugin(userId, pluginSlug);

        if (isOwner)
        {
            TempData[TempDataConstant.WarningMessage] = "You cannot review your own plugin.";
            var backUrl = Url.Action(nameof(GetPluginDetails), "Home",
                new { pluginSlug = pluginSlug.ToString() });
            return Redirect(backUrl ?? "/");
        }

        if (!await userVerifiedLogic.IsUserGithubVerified(User, conn))
        {
            TempData[TempDataConstant.WarningMessage] =
                "You need to verify your GitHub account in order to review plugins";
            return RedirectToAction("AccountDetails", "Account");
        }

        int[]? pluginVersionParts = null;
        if (!string.IsNullOrWhiteSpace(pluginVersion) &&
            PluginVersion.TryParse(pluginVersion, out var v))
        {
            pluginVersionParts = v.VersionParts;
        }

        await conn.UpsertPluginReview(pluginSlug, userId, rating, body, pluginVersionParts);

        var sort = Request.Query["sort"].ToString();
        var url = Url.Action(nameof(GetPluginDetails), "Home",
            new { pluginSlug = pluginSlug.ToString(), sort = string.IsNullOrEmpty(sort) ? null : sort });

        return Redirect((url ?? "/") + "#reviews");
    }

    [HttpPost("public/plugins/{pluginSlug}/reviews/{id:long}/vote")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> VoteReview(
        [ModelBinder(typeof(PluginSlugModelBinder))] PluginSlug pluginSlug,
        long id,
        bool isHelpful)
    {
        var userId = userManager.GetUserId(User);
        if (string.IsNullOrEmpty(userId)) return Forbid();

        await using var conn = await connectionFactory.Open();

        var current = await conn.GetReviewHelpfulVoteAsync(pluginSlug, id, userId);

        var ok = current == isHelpful
            ? await conn.RemoveReviewHelpfulVoteAsync(pluginSlug, id, userId)
            : await conn.UpsertReviewHelpfulVoteAsync(pluginSlug, id, userId, isHelpful);

        if (!ok)
            TempData[TempDataConstant.WarningMessage] = "Error while updating review helpful vote";

        var url = Url.Action(nameof(GetPluginDetails), new { pluginSlug = pluginSlug.ToString() });
        return Redirect((url ?? "/") + "#reviews");
    }

    [HttpPost("public/plugins/{pluginSlug}/reviews/{id:long}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteReview(
        [ModelBinder(typeof(PluginSlugModelBinder))] PluginSlug pluginSlug,
        long id)
    {
        var userId = userManager.GetUserId(User);
        if (string.IsNullOrEmpty(userId)) return Forbid();

        var isAdmin = User.Identity?.IsAuthenticated == true && User.IsInRole(Roles.ServerAdmin);

        await using var conn = await connectionFactory.Open();

        var ok = await conn.DeleteReviewAsync(pluginSlug, id, userId, isAdmin);

        if (!ok)
            TempData[TempDataConstant.WarningMessage] = "Error while deleting review";

        var url = Url.Action(nameof(GetPluginDetails), new { pluginSlug = pluginSlug.ToString() });
        return Redirect((url ?? "/") + "#reviews");
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

    static string? GetGithubHandle(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();

        if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            var raw = url.TrimStart('/');
            if (raw.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase) ||
                         raw.StartsWith("www.github.com/", StringComparison.OrdinalIgnoreCase))
                {
                if (!Uri.TryCreate("https://" + raw, UriKind.Absolute, out u))
                    return null;
                }
            else
            {
                if (!Uri.TryCreate("https://github.com/" + raw, UriKind.Absolute, out u))
                    return null;
            }
        }

        if (!u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || u.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase))
            return null;

        var segs = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segs.Length == 0) return null;

        var handle = segs[0];
        if (handle.Equals("orgs", StringComparison.OrdinalIgnoreCase) ||
            handle.Equals("users", StringComparison.OrdinalIgnoreCase))
            return null;

        return handle;
    }

    static GitHubContributor? GetGithubIdentity(string? githubUrl, int size = 48)
    {
        var handle = GetGithubHandle(githubUrl);
        if (string.IsNullOrWhiteSpace(handle)) return null;

        var safe = Uri.EscapeDataString(handle);
        return new GitHubContributor
        {
            Login       = handle,
            HtmlUrl     = $"https://github.com/{safe}",
            AvatarUrl   = $"https://avatars.githubusercontent.com/{safe}?s={size}",
            UserViewType= "user",
            Contributions = 0
        };
    }


    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View("Error", new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
