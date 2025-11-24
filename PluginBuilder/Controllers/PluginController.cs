using System.Text;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Npgsql;
using PluginBuilder.Components.PluginVersion;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.DataModels;
using PluginBuilder.JsonConverters;
using PluginBuilder.ModelBinders;
using PluginBuilder.Services;
using PluginBuilder.Util;
using PluginBuilder.Util.Extensions;
using PluginBuilder.ViewModels;
using PluginBuilder.ViewModels.Plugin;
using static Dapper.SqlMapper;

namespace PluginBuilder.Controllers;

[Authorize(Policy = Policies.OwnPlugin)]
[Route("/plugins/{pluginSlug}")]
public class PluginController(
    DBConnectionFactory connectionFactory,
    UserManager<IdentityUser> userManager,
    EmailService emailService,
    BuildService buildService,
    GPGKeyService gpgKeyService,
    AzureStorageClient azureStorageClient,
    UserVerifiedLogic userVerifiedLogic,
    IUserClaimsPrincipalFactory<IdentityUser> principalFactory,
    IOutputCacheStore outputCacheStore)
    : Controller
{
    [HttpGet("settings")]
    public async Task<IActionResult> Settings(
        [ModelBinder(typeof(PluginSlugModelBinder))]
        PluginSlug pluginSlug)
    {
        var userId = userManager.GetUserId(User);
        await using var conn = await connectionFactory.Open();
        var settings = await conn.GetSettings(pluginSlug);

        if (settings is null)
            return NotFound();

        var pluginOwner = await conn.RetrievePluginPrimaryOwner(pluginSlug);
        var vm = settings.ToPluginSettingViewModel();
        vm.IsPluginPrimaryOwner = pluginOwner == userId;
        return View(vm);
    }

    [HttpPost("settings")]
    public async Task<IActionResult> Settings(
        [ModelBinder(typeof(PluginSlugModelBinder))]
        PluginSlug pluginSlug,
        PluginSettingViewModel settingViewModel, [FromForm] bool removeLogoFile = false)
    {
        if (settingViewModel is null)
            return NotFound();

        if (string.IsNullOrEmpty(settingViewModel.GitRepository) || !Uri.TryCreate(settingViewModel.GitRepository, UriKind.Absolute, out _))
        {
            ModelState.AddModelError(nameof(settingViewModel.GitRepository), "Git repository is required and should be an absolute URL");
            return View(settingViewModel);
        }
        if (!string.IsNullOrEmpty(settingViewModel.Documentation) && !Uri.TryCreate(settingViewModel.Documentation, UriKind.Absolute, out _))
        {
            ModelState.AddModelError(nameof(settingViewModel.Documentation), "Documentation should be an absolute URL");
            return View(settingViewModel);
        }
        var userId = userManager.GetUserId(User);
        await using var conn = await connectionFactory.Open();
        var existingSetting = await conn.GetSettings(pluginSlug);
        var pluginOwner = await conn.RetrievePluginPrimaryOwner(pluginSlug);
        settingViewModel.LogoUrl = existingSetting?.Logo;
        settingViewModel.IsPluginPrimaryOwner = pluginOwner == userId;
        if (settingViewModel.IsPluginPrimaryOwner && (string.IsNullOrEmpty(settingViewModel.Description) || string.IsNullOrEmpty(settingViewModel.PluginTitle)))
        {
            ModelState.AddModelError(nameof(settingViewModel.PluginTitle), "Plugin title and description are required");
            return View(settingViewModel);
        }

        if (settingViewModel.Logo != null)
        {
            if (!settingViewModel.Logo.ValidateUploadedImage(out string errorMessage))
            {
                ModelState.AddModelError(nameof(settingViewModel.Logo), $"Image upload validation failed: {errorMessage}");
                return View(settingViewModel);
            }
            try
            {
                var uniqueBlobName = $"{pluginSlug}-{Guid.NewGuid()}{Path.GetExtension(settingViewModel.Logo.FileName)}";
                settingViewModel.LogoUrl = await azureStorageClient.UploadImageFile(settingViewModel.Logo, uniqueBlobName);
            }
            catch (Exception)
            {
                ModelState.AddModelError(nameof(settingViewModel.LogoUrl), "Could not complete settings upload. An error occurred while uploading logo");
                return View(settingViewModel);
            }
        }
        else if (removeLogoFile)
        {
            settingViewModel.Logo = null;
            settingViewModel.LogoUrl = null;
        }
        if (!settingViewModel.IsPluginPrimaryOwner && existingSetting is not null)
        {
            settingViewModel.RequireGPGSignatureForRelease = existingSetting.RequireGPGSignatureForRelease;
            settingViewModel.PluginTitle = existingSetting.PluginTitle;
            settingViewModel.Description = existingSetting.Description;
        }
        await conn.SetPluginSettings(pluginSlug, settingViewModel.ToPluginSettings());
        await outputCacheStore.EvictByTagAsync(CacheTags.Plugins, CancellationToken.None);
        TempData[TempDataConstant.SuccessMessage] = "Settings updated successfully";
        return RedirectToAction(nameof(Settings), new { pluginSlug });
    }

    [HttpGet("create")]
    public async Task<IActionResult> CreateBuild(
        [ModelBinder(typeof(PluginSlugModelBinder))]
        PluginSlug pluginSlug, long? copyBuild = null)
    {
        await using var conn = await connectionFactory.Open();
        if (!await userVerifiedLogic.IsUserEmailVerifiedForPublish(User) || !await userVerifiedLogic.IsUserGithubVerified(User, conn))
        {
            TempData[TempDataConstant.WarningMessage] = "You need to verify your email address and github account in order to create and publish plugins";
            return RedirectToAction("AccountDetails", "Account");
        }

        var settings = await conn.GetSettings(pluginSlug);
        CreateBuildViewModel model = new()
        {
            GitRepository = settings?.GitRepository,
            GitRef = settings?.GitRef,
            PluginDirectory = settings?.PluginDirectory,
            BuildConfig = settings?.BuildConfig
        };

        if (copyBuild is long buildId)
        {
            var buildInfo = await conn.QueryFirstOrDefaultAsync<string>("SELECT build_info FROM builds WHERE plugin_slug=@pluginSlug AND id=@buildId",
                new { buildId, pluginSlug = pluginSlug.ToString() });
            if (buildInfo != null)
            {
                var bi = BuildInfo.Parse(buildInfo);
                model.GitRepository = bi.GitRepository;
                model.GitRef = bi.GitRef;
                model.PluginDirectory = bi.PluginDir;
                model.BuildConfig = bi.BuildConfig;
            }
        }

        return View(model);
    }

    [HttpPost("create")]
    public async Task<IActionResult> CreateBuild(
        [ModelBinder(typeof(PluginSlugModelBinder))]
        PluginSlug pluginSlug,
        CreateBuildViewModel model)
    {
        if (!ModelState.IsValid)
            return View(model);
        await using var conn = await connectionFactory.Open();
        if (!await userVerifiedLogic.IsUserEmailVerifiedForPublish(User))
        {
            TempData[TempDataConstant.WarningMessage] = "You need to verify your email address in order to create and publish plugins";
            return RedirectToAction("AccountDetails", "Account");
        }
        try
        {
            var identifier = await buildService.FetchIdentifierFromGithubCsprojAsync(
                model.GitRepository,
                model.GitRef,
                model.PluginDirectory
            );

            var owns = await conn.EnsureIdentifierOwnership(pluginSlug, identifier);
            if (!owns)
            {
                TempData[TempDataConstant.WarningMessage] = $"The plugin identifier '{identifier}' does not belong to plugin slug '{pluginSlug}'.";
                return View(model);
            }
        }
        catch (BuildServiceException ex)
        {
            TempData[TempDataConstant.WarningMessage] = $"Manifest validation failed: {ex.Message}";
            return View(model);
        }

        var buildId = await conn.NewBuild(pluginSlug, model.ToBuildParameter());
        if (buildId == 0)
        {
            var existingSetting = await conn.GetSettings(pluginSlug) ?? new();
            existingSetting.GitRepository = model.GitRepository;
            existingSetting.GitRef = model.GitRef;
            existingSetting.PluginDirectory = model.PluginDirectory;
            existingSetting.BuildConfig = model.BuildConfig;
            await conn.SetPluginSettings(pluginSlug, existingSetting);
        }
        _ = buildService.Build(new FullBuildId(pluginSlug, buildId));
        return RedirectToAction(nameof(Build), new { pluginSlug = pluginSlug.ToString(), buildId });
    }

    [HttpGet("request-listing")]
    public async Task<IActionResult> RequestListing(
       [ModelBinder(typeof(PluginSlugModelBinder))]
        PluginSlug pluginSlug)
    {
        var model = new RequestListingViewModel { PluginSlug = pluginSlug.ToString() };
        await using var conn = await connectionFactory.Open();
        var plugin = await conn.GetPluginDetails(pluginSlug);
        if (plugin is null)
            return NotFound();

        if (plugin.Visibility == PluginVisibilityEnum.Listed)
            return RedirectToAction(nameof(Dashboard), new { pluginSlug });

        if (plugin.Visibility == PluginVisibilityEnum.Hidden)
            return NotFound();

        var pluginOwners = await conn.GetPluginOwners(pluginSlug);
        var pluginSettings = SafeJson.Deserialize<PluginSettings>(plugin.Settings);
        var pendingRequest = await conn.GetPendingOrRejectedListingRequestForPlugin(pluginSlug);
        model.ReleaseNote = pluginSettings?.Description;
        if (pendingRequest != null)
        {
            model.RejectedListing = pendingRequest.Status == PluginListingRequestStatus.Rejected;
            model.PendingListing = pendingRequest.Status == PluginListingRequestStatus.Pending;
            model.TelegramVerificationMessage = pendingRequest.TelegramVerificationMessage;
            model.UserReviews = pendingRequest.UserReviews;
            model.ReleaseNote = pendingRequest.ReleaseNote;
            model.AnnouncementDate = pendingRequest.AnnouncementDate;
            var now = DateTimeOffset.UtcNow;
            model.CanSendEmailReminder = now >= pendingRequest.SubmittedAt.AddDays(1);
            TempData[TempDataConstant.WarningMessage] = pendingRequest.Status switch
            {
                PluginListingRequestStatus.Pending => "Your listing request has been sent and is pending validation",
                PluginListingRequestStatus.Rejected => "Your listing request was not approved. Please review the feedback and resubmit when ready.",
                _ => "Your listing request is under review"
            };
        }
        model = await ListingRequirementsMet(conn, pluginSettings, pluginOwners, model);
        return View(model);
    }

    [HttpPost("request-listing")]
    public async Task<IActionResult> RequestListing(
        [ModelBinder(typeof(PluginSlugModelBinder))]
        PluginSlug pluginSlug, RequestListingViewModel model)
    {
        await using var conn = await connectionFactory.Open();
        var plugin = await conn.GetPluginDetails(pluginSlug);
        if (plugin is null)
            return NotFound();
            
        var pluginSettings = SafeJson.Deserialize<PluginSettings>(plugin.Settings);
        if (plugin.Visibility == PluginVisibilityEnum.Hidden)
            return NotFound();

        if (plugin.Visibility == PluginVisibilityEnum.Listed)
            return RedirectToAction(nameof(Dashboard), new { pluginSlug });

        if (string.IsNullOrWhiteSpace(model.ReleaseNote))
            ModelState.AddModelError(nameof(model.ReleaseNote), "Description is required.");

        if (string.IsNullOrWhiteSpace(model.TelegramVerificationMessage) || !Uri.TryCreate(model.TelegramVerificationMessage, UriKind.Absolute, out var telegramUri) ||
            telegramUri.Scheme != Uri.UriSchemeHttps ||
            !telegramUri.Host.Equals("t.me", StringComparison.OrdinalIgnoreCase) || !telegramUri.AbsolutePath.Trim('/').StartsWith("btcpayserver", StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(nameof(model.TelegramVerificationMessage), "Telegram verification message on BTCPay Server telegram (https://t.me/btcpayserver/... ) channel is required.");
        }
        if (string.IsNullOrWhiteSpace(model.UserReviews))
            ModelState.AddModelError(nameof(model.UserReviews), "User-reviews link is required.");

        if (!ModelState.IsValid)
        {
            var owners = await conn.GetPluginOwners(pluginSlug);
            model.PendingListing = await conn.HasPendingListingRequest(pluginSlug);
            model = await ListingRequirementsMet(conn, pluginSettings, owners, model);
            return View(model);
        }
        await conn.CreateListingRequest(pluginSlug, model.ReleaseNote.Trim(), model.TelegramVerificationMessage.Trim(), model.UserReviews.Trim(), model.AnnouncementDate);
        await SendRequestListingEmail(conn, pluginSlug.ToString());
        TempData[TempDataConstant.SuccessMessage] = "Your listing request has been sent and is pending validation";
        return RedirectToAction(nameof(Dashboard), new { pluginSlug });
    }

    private async Task<RequestListingViewModel> ListingRequirementsMet(NpgsqlConnection conn, PluginSettings pluginSettings, List<OwnerVm> owners, RequestListingViewModel model)
    {
        if (pluginSettings == null || owners == null || owners.Count == 0)
        {
            model.Step = RequestListingViewModel.State.Invalid;
            return model;
        }

        var docsMissing = string.IsNullOrWhiteSpace(pluginSettings?.GitRepository) || string.IsNullOrWhiteSpace(pluginSettings?.Documentation)
            || string.IsNullOrWhiteSpace(pluginSettings?.Logo) || string.IsNullOrWhiteSpace(pluginSettings?.Description);

        var ownerNotVerified = false;
        foreach (var owner in owners)
        {
            if (!await conn.IsSocialAccountsVerified(owner.UserId))
            {
                ownerNotVerified = true;
                break;
            }
        }
        model.Step = (docsMissing, ownerNotVerified) switch
        {
            (true, _) => RequestListingViewModel.State.UpdatePluginSettings,
            (false, true) => RequestListingViewModel.State.UpdateOwnerAccountSettings,
            (false, false) => RequestListingViewModel.State.Done
        };
        return model;
    }

    public async Task<IActionResult> SendReminder([ModelBinder(typeof(PluginSlugModelBinder))] PluginSlug pluginSlug)
    {
        await using var conn = await connectionFactory.Open();
        var plugin = await conn.GetPluginDetails(pluginSlug);
        if (plugin is null || plugin.Visibility != PluginVisibilityEnum.Unlisted)
            return NotFound();

        var request = await conn.GetPendingOrRejectedListingRequestForPlugin(pluginSlug);
        if (request is null)
        {
            TempData[TempDataConstant.WarningMessage] = "No listing request exist";
            return RedirectToAction(nameof(Dashboard), new { pluginSlug });
        }
        var now = DateTimeOffset.UtcNow;
        if (now < request.SubmittedAt.AddDays(1))
        {
            TempData[TempDataConstant.WarningMessage] = "Please wait 24 hours before sending another reminder";
            return RedirectToAction(nameof(Dashboard), new { pluginSlug });
        }
        await SendRequestListingEmail(conn, pluginSlug.ToString());
        TempData[TempDataConstant.SuccessMessage] = "Request listing reminders sent to admins";
        return RedirectToAction(nameof(RequestListing), new { pluginSlug });
    }

    private async Task SendRequestListingEmail(NpgsqlConnection conn, string pluginSlug)
    {
        var pluginPublicUrl = Url.Action(nameof(HomeController.GetPluginDetails), "Home", new { pluginSlug }, Request.Scheme);
        var listingReviewUrl = Url.Action(nameof(AdminController.ListingRequests), "Admin", new { }, Request.Scheme);
        await emailService.NotifyAdminOnNewRequestListing(conn, pluginSlug, pluginPublicUrl!, listingReviewUrl!);
    }

    [HttpPost("versions/{version}/release")]
    public async Task<IActionResult> Release(
        [ModelBinder(typeof(PluginSlugModelBinder))]
        PluginSlug pluginSlug,
        [ModelBinder(typeof(PluginVersionModelBinder))]
        PluginVersion version, string command, IFormFile? signatureFile)
    {
        await using var conn = await connectionFactory.Open();

        var pluginBuild = await conn.QueryFirstOrDefaultAsync<(long buildId, string identifier)>(
                "SELECT v.build_id, p.identifier FROM versions v JOIN plugins p ON v.plugin_slug = p.slug WHERE plugin_slug=@pluginSlug AND ver=@version",
                new { pluginSlug = pluginSlug.ToString(), version = version.VersionParts });

        var pluginSettings = await conn.GetSettings(pluginSlug);

        switch (command)
        {
            case "remove":
                FullBuildId fullBuildId = new(pluginSlug, pluginBuild.buildId);
                await conn.ExecuteAsync("DELETE FROM versions WHERE plugin_slug=@pluginSlug AND ver=@version",
                    new { pluginSlug = pluginSlug.ToString(), version = version.VersionParts });
                await buildService.UpdateBuild(fullBuildId, BuildStates.Removed, null);
                await outputCacheStore.EvictByTagAsync(CacheTags.Plugins, CancellationToken.None);
                return RedirectToAction(nameof(Build), new { pluginSlug = pluginSlug.ToString(), pluginBuild.buildId });

            case "sign_release":
                var manifest_info = await conn.QueryFirstOrDefaultAsync<string>("SELECT manifest_info FROM builds b WHERE b.plugin_slug=@pluginSlug AND b.id=@buildId LIMIT 1",
                    new { pluginSlug = pluginSlug.ToString(), pluginBuild.buildId });

                if (signatureFile is null)
                {
                    TempData[TempDataConstant.WarningMessage] = "Signature file is required";
                    return RedirectToAction(nameof(Version), new { pluginSlug = pluginSlug.ToString(), version = version.ToString() });
                }
                var message = GetManifestHash(NiceJson(manifest_info), true);
                if (string.IsNullOrEmpty(message))
                {
                    TempData[TempDataConstant.WarningMessage] = "manifest information for plugin not available";
                    return RedirectToAction(nameof(Version), new { pluginSlug = pluginSlug.ToString(), version = version.ToString() });
                }
                var signatureVerification = await gpgKeyService.VerifyDetachedSignature(pluginSlug.ToString(), userManager.GetUserId(User)!, Encoding.UTF8.GetBytes(message), signatureFile);
                if (!signatureVerification.valid)
                {
                    TempData[TempDataConstant.WarningMessage] = signatureVerification.message;
                    return RedirectToAction(nameof(Version), new { pluginSlug = pluginSlug.ToString(), version = version.ToString() });
                }
                await conn.UpdateVersionReleaseStatus(pluginSlug, command, version, signatureVerification.proof);
                break;

            default:
                if (pluginSettings?.RequireGPGSignatureForRelease == true && command == "release")
                {
                    TempData[TempDataConstant.WarningMessage] = "A verified GPG signature is required to release this version";
                    return RedirectToAction(nameof(Version), new { pluginSlug = pluginSlug.ToString(), version = version.ToString() });
                }
                await conn.UpdateVersionReleaseStatus(pluginSlug, command, version);
                break;
        }

        await outputCacheStore.EvictByTagAsync(CacheTags.Plugins, CancellationToken.None);
        TempData[TempDataConstant.SuccessMessage] = $"Version {version} {(command is "release" or "sign_release" ? "released" : "unreleased")}";
        return RedirectToAction(nameof(Version), new { pluginSlug = pluginSlug.ToString(), version = version.ToString() });
    }

    [HttpGet("versions/{version}")]
    public async Task<IActionResult> Version(
        [ModelBinder(typeof(PluginSlugModelBinder))]
        PluginSlug pluginSlug,
        [ModelBinder(typeof(PluginVersionModelBinder))]
        PluginVersion version)
    {
        await using var conn = await connectionFactory.Open();
        var buildId = conn.ExecuteScalar<long>("SELECT build_id FROM versions WHERE plugin_slug=@pluginSlug AND ver=@version",
            new { pluginSlug = pluginSlug.ToString(), version = version.VersionParts });
        return RedirectToAction(nameof(Build), new { pluginSlug = pluginSlug.ToString(), buildId });
    }

    [HttpGet("builds/{buildId}")]
    public async Task<IActionResult> Build(
        [ModelBinder(typeof(PluginSlugModelBinder))]
        PluginSlug pluginSlug,
        long buildId)
    {
        await using var conn = await connectionFactory.Open();
        var row =
            await conn
                .QueryFirstOrDefaultAsync<(string manifest_info, string build_info, string state, DateTimeOffset created_at, bool published, bool pre_release, string signatureproof)>(
                    "SELECT manifest_info, build_info, state, created_at, v.ver IS NOT NULL, v.pre_release, v.signatureproof FROM builds b " +
                    "LEFT JOIN versions v ON b.plugin_slug=v.plugin_slug AND b.id=v.build_id " +
                    "WHERE b.plugin_slug=@pluginSlug AND id=@buildId " +
                    "LIMIT 1",
                    new { pluginSlug = pluginSlug.ToString(), buildId });
        var logLines = await conn.QueryAsync<string>(
            "SELECT logs FROM builds_logs " +
            "WHERE plugin_slug=@pluginSlug AND build_id=@buildId " +
            "ORDER BY created_at;",
            new { pluginSlug = pluginSlug.ToString(), buildId });
        var logs = string.Join("\r\n", logLines);
        var pluginSetting = await conn.GetSettings(pluginSlug);
        var signatureProof = SafeJson.Deserialize<SignatureProof>(row.signatureproof);

        BuildViewModel vm = new();
        var buildInfo = row.build_info is null ? null : BuildInfo.Parse(row.build_info);
        var manifest = row.manifest_info is null ? null : PluginManifest.Parse(row.manifest_info);
        vm.FullBuildId = new FullBuildId(pluginSlug, buildId);
        vm.ManifestInfo = NiceJson(row.manifest_info, signatureProof?.Fingerprint);
        vm.BuildInfo = buildInfo?.ToString(Formatting.Indented);
        vm.DownloadLink = buildInfo?.Url;
        vm.State = row.state;
        vm.CreatedDate = (DateTimeOffset.UtcNow - row.created_at).ToTimeAgo();
        vm.Commit = buildInfo?.GitCommit?.Substring(0, 8);
        vm.Repository = buildInfo?.GitRepository;
        vm.GitRef = buildInfo?.GitRef;
        vm.Version = PluginVersionViewModel.CreateOrNull(manifest?.Version?.ToString(), row.published, row.pre_release, row.state, pluginSlug.ToString());
        vm.RepositoryLink = GetUrl(buildInfo);
        vm.DownloadLink = buildInfo?.Url;
        //vm.Error = buildInfo?.Error;
        vm.RequireGPGSignatureForRelease = pluginSetting?.RequireGPGSignatureForRelease ?? false;
        vm.ManifestInfoSha256Hash = GetManifestHash(NiceJson(row.manifest_info), vm.RequireGPGSignatureForRelease);
        vm.Published = row.published;
        //var buildId = await conn.NewBuild(pluginSlug);
        //_ = buildService.Build(new FullBuildId(pluginSlug, buildId), model.ToBuildParameter());
        if (logs != "")
            vm.Logs = logs;
        return View(vm);
    }


    private string GetManifestHash(string? manifestInfo, bool requiresGPGSignature)
    {
        if (!requiresGPGSignature || string.IsNullOrEmpty(manifestInfo))
            return string.Empty;

        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(manifestInfo));
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    private string? NiceJson(string? json, string? fingerprint = null)
    {
        if (json is null)
            return null;
        var data = JObject.Parse(json);
        data = new JObject(data.Properties().OrderBy(p => p.Name));
        if (!string.IsNullOrWhiteSpace(fingerprint))
            data["SignatureFingerprint"] = fingerprint;
        return data.ToString(Formatting.Indented);
    }

    [HttpGet("")]
    public async Task<IActionResult> Dashboard(
        [ModelBinder(typeof(PluginSlugModelBinder))]
        PluginSlug pluginSlug)
    {
        await using var conn = await connectionFactory.Open();
        var rows =
            await conn
                .QueryAsync<(long id, string state, string? manifest_info, string? build_info, DateTimeOffset created_at, bool published, bool pre_release)>
                ("SELECT id, state, manifest_info, build_info, created_at, v.ver IS NOT NULL, v.pre_release " +
                 "FROM builds b " +
                 "LEFT JOIN versions v ON b.plugin_slug=v.plugin_slug AND b.id=v.build_id " +
                 "WHERE b.plugin_slug = @pluginSlug " +
                 "ORDER BY id DESC " +
                 "LIMIT 50", new { pluginSlug = pluginSlug.ToString() });
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
            b.Version = PluginVersionViewModel.CreateOrNull(manifest?.Version?.ToString(), row.published, row.pre_release, row.state, pluginSlug.ToString());
            b.Date = (DateTimeOffset.UtcNow - row.created_at).ToTimeAgo();
            b.RepositoryLink = GetUrl(buildInfo);
            b.DownloadLink = buildInfo?.Url;
            b.Error = buildInfo?.Error;
        }
        var pluginSettings = await conn.GetPluginDetails(pluginSlug);
        vm.RequestListing = pluginSettings?.Visibility == PluginVisibilityEnum.Unlisted;
        return View(vm);
    }

    public static string? GetUrl(BuildInfo? buildInfo)
    {
        if (buildInfo?.GitRepository is string repo && buildInfo?.GitCommit is string commit)
        {
            string? repoName = null;
            // git@github.com:Kukks/btcpayserver.git
            if (repo.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
                repoName = repo.Substring("git@github.com:".Length);
            // https://github.com/Kukks/btcpayserver.git
            // https://github.com/Kukks/btcpayserver
            else if (repo.StartsWith("https://github.com/")) repoName = repo.Substring("https://github.com/".Length);
            if (repoName is not null)
            {
                // Kukks/btcpayserver
                if (repoName.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                    repoName = repoName.Substring(0, repoName.Length - 4);
                // https://github.com/Kukks/btcpayserver/tree/plugins/collection/Plugins/BTCPayServer.Plugins.AOPP
                var link = $"https://github.com/{repoName}/tree/{commit}";
                if (buildInfo?.PluginDir is string pluginDir)
                    link += $"/{pluginDir}";
                return link;
            }
        }

        return null;
    }

    [HttpGet("owners")]
    public async Task<IActionResult> Owners([ModelBinder(typeof(PluginSlugModelBinder))] PluginSlug pluginSlug)
    {
        var currentUserId = userManager.GetUserId(User) ?? throw new InvalidOperationException();

        await using var conn = await connectionFactory.Open();

        var owners = await conn.GetPluginOwners(pluginSlug);

        var vm = new PluginOwnersPageViewModel
        {
            PluginSlug     = pluginSlug.ToString(),
            CurrentUserId  = currentUserId,
            IsPrimaryOwner = owners.Any(o => o.UserId == currentUserId && o.IsPrimary),
            Owners         = owners
        };

        return View(vm);
    }

    [HttpPost("owners")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddOwner([ModelBinder(typeof(PluginSlugModelBinder))] PluginSlug pluginSlug, [FromForm] string email)
    {
        try
        {
            await using var conn = await connectionFactory.Open();

            var primaryOwner = await conn.RetrievePluginPrimaryOwner(pluginSlug);

            if (primaryOwner != userManager.GetUserId(User))
            {
                TempData[TempDataConstant.WarningMessage] = "Only primary owners can add new owners.";
                return RedirectToAction(nameof(Owners), new { pluginSlug });
            }

            email = email.Trim();

            if (string.IsNullOrWhiteSpace(email))
            {
                TempData[TempDataConstant.WarningMessage] = "Email cannot be empty.";
                return RedirectToAction(nameof(Owners), new { pluginSlug });
            }

            var user = await userManager.FindByEmailAsync(email);

            if (user is null)
            {
                TempData[TempDataConstant.WarningMessage] = "User not found.";
                return RedirectToAction(nameof(Owners), new { pluginSlug });
            }

            if (await conn.UserOwnsPlugin(user.Id, pluginSlug))
            {
                TempData[TempDataConstant.WarningMessage] = "User is already an owner.";
                return RedirectToAction(nameof(Owners), new { pluginSlug });
            }

            var targetPrincipal = await principalFactory.CreateAsync(user);

            if (!await userVerifiedLogic.IsUserEmailVerifiedForPublish(targetPrincipal))
            {
                TempData[TempDataConstant.WarningMessage] = "Owner must have a confirmed email.";
                return RedirectToAction(nameof(Owners), new { pluginSlug });
            }

            if (!await userVerifiedLogic.IsUserGithubVerified(targetPrincipal, conn))
            {
                TempData[TempDataConstant.WarningMessage] = "Owner must have a verified Github account.";
                return RedirectToAction(nameof(Owners), new { pluginSlug });
            }

            await conn.AddUserPlugin(pluginSlug, user.Id);
            TempData[TempDataConstant.SuccessMessage] = "User added.";
        }
        catch (InvalidOperationException ex) { TempData[TempDataConstant.WarningMessage] = ex.Message; }
        return RedirectToAction(nameof(Owners), new { pluginSlug });
    }

    [HttpPost("owners/{userId}/transfer-primary-owner")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TransferPrimaryOwner([ModelBinder(typeof(PluginSlugModelBinder))] PluginSlug pluginSlug, string userId)
    {
        try
        {
            await using var conn = await connectionFactory.Open();

            var currentPrimaryId = await conn.RetrievePluginPrimaryOwner(pluginSlug);
            if (currentPrimaryId != userManager.GetUserId(User))
            {
                TempData[TempDataConstant.WarningMessage] = "Only the primary owner can transfer primary.";
                return RedirectToAction(nameof(Owners), new { pluginSlug });
            }

            if (!await conn.UserOwnsPlugin(userId, pluginSlug))
            {
                TempData[TempDataConstant.WarningMessage] = "Target user is not an owner";
                return RedirectToAction(nameof(Owners), new { pluginSlug });
            }

            var ok = await conn.AssignPluginPrimaryOwner(pluginSlug, userId);

            if (!ok)
            {
                TempData[TempDataConstant.WarningMessage] = "Failed to assign primary owner.";
                return RedirectToAction(nameof(Owners), new { pluginSlug });
            }

            TempData[TempDataConstant.SuccessMessage] = "Primary owner transferred.";
        }
        catch (InvalidOperationException ex) { TempData[TempDataConstant.WarningMessage] = ex.Message; }
        return RedirectToAction(nameof(Owners), new { pluginSlug });
    }

    [HttpPost("owners/{userId}/remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveOwner([ModelBinder(typeof(PluginSlugModelBinder))] PluginSlug pluginSlug, string userId)
    {
        try
        {
            var currentUserId = userManager.GetUserId(User);
            await using var conn = await connectionFactory.Open();
            await using var tx = await conn.BeginTransactionAsync();

            var owners = (await conn.QueryAsync<(string UserId, bool IsPrimary)>(
                """
                SELECT user_id AS UserId, is_primary_owner AS IsPrimary
                          FROM users_plugins
                          WHERE plugin_slug = @slug
                          FOR UPDATE;
                """,
                new { slug = pluginSlug.ToString() }, tx)).ToList();

            if (owners.All(o => o.UserId != userId))
            {
                TempData[TempDataConstant.WarningMessage] = "User not an owner.";
                return RedirectToAction(nameof(Owners), new { pluginSlug });
            }

            var primaryId = owners.FirstOrDefault(o => o.IsPrimary).UserId;

            var currentIsPrimary = primaryId == currentUserId;
            if (!currentIsPrimary && userId != currentUserId)
            {
                TempData[TempDataConstant.WarningMessage] = "Only primary owner can remove other owners.";
                return RedirectToAction(nameof(Owners), new { pluginSlug });
            }

            if (userId == primaryId)
            {
                TempData[TempDataConstant.WarningMessage] = "Primary owner cannot be removed.";
                return RedirectToAction(nameof(Owners), new { pluginSlug });
            }

            if (owners.Count <= 1)
            {
                TempData[TempDataConstant.WarningMessage] = "Cannot remove the last owner.";
                return RedirectToAction(nameof(Owners), new { pluginSlug });
            }

            var deleted = await conn.RemovePluginOwner(pluginSlug, userId);

            if (deleted != 1)
            {
                TempData[TempDataConstant.WarningMessage] = "Failed to remove owner.";
                return RedirectToAction(nameof(Owners), new { pluginSlug });
            }

            await tx.CommitAsync();
            TempData[TempDataConstant.SuccessMessage] = "Owner removed.";
        }
        catch (InvalidOperationException ex)
        {
            TempData[TempDataConstant.WarningMessage] = ex.Message;
        }
        return RedirectToAction(nameof(Owners), new { pluginSlug });
    }
}
