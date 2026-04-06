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

namespace PluginBuilder.Controllers;

[Authorize(Policy = Policies.OwnPlugin)]
[Route("/plugins/{pluginSlug}")]
public class PluginController(
    DBConnectionFactory connectionFactory,
    UserManager<IdentityUser> userManager,
    EmailService emailService,
    BuildService buildService,
    AzureStorageClient azureStorageClient,
    UserVerifiedLogic userVerifiedLogic,
    IOutputCacheStore outputCacheStore,
    PluginOwnershipService ownershipService,
    VersionLifecycleService versionLifecycleService,
    ILogger<PluginController> logger)
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
        PluginSettingViewModel settingViewModel,
        [FromForm] bool removeLogoFile = false,
        [FromForm] string? removeImageUrl = null,
        [FromForm] List<string>? imagesUrl = null,
        [FromForm] bool imagesUrlSubmitted = false,
        [FromForm] List<string>? imagesOrder = null)
    {
        if (settingViewModel is null)
            return NotFound();

        if (string.IsNullOrEmpty(settingViewModel.GitRepository) ||
            !Uri.TryCreate(settingViewModel.GitRepository, UriKind.Absolute, out var gitRepoUri) ||
            gitRepoUri.Scheme != Uri.UriSchemeHttps)
        {
            ModelState.AddModelError(nameof(settingViewModel.GitRepository), "Git repository is required and must be an HTTPS URL");
            return View(settingViewModel);
        }

        if (!string.IsNullOrEmpty(settingViewModel.Documentation) &&
            (!Uri.TryCreate(settingViewModel.Documentation, UriKind.Absolute, out var docUri) ||
             docUri.Scheme != Uri.UriSchemeHttps))
        {
            ModelState.AddModelError(nameof(settingViewModel.Documentation), "Documentation must be an HTTPS URL");
            return View(settingViewModel);
        }

        if (!string.IsNullOrEmpty(settingViewModel.VideoUrl))
        {
            if (!Uri.TryCreate(settingViewModel.VideoUrl, UriKind.Absolute, out var videoUri) || videoUri.Scheme != Uri.UriSchemeHttps)
            {
                ModelState.AddModelError(nameof(settingViewModel.VideoUrl), "Video URL must be a valid HTTPS URL");
                return View(settingViewModel);
            }

            if (!settingViewModel.VideoUrl.IsSupportedVideoUrl())
            {
                ModelState.AddModelError(nameof(settingViewModel.VideoUrl), "Video URL must be from a supported platform (YouTube, Vimeo)");
                return View(settingViewModel);
            }
        }

        var userId = userManager.GetUserId(User);
        await using var conn = await connectionFactory.Open();
        var existingSetting = await conn.GetSettings(pluginSlug);
        var pluginOwner = await conn.RetrievePluginPrimaryOwner(pluginSlug);
        settingViewModel.LogoUrl = existingSetting?.Logo;
        settingViewModel.IsPluginPrimaryOwner = pluginOwner == userId;

        var existingImages = existingSetting?.Images ?? [];
        var existingImagesSet = new HashSet<string>(existingImages, StringComparer.Ordinal);
        settingViewModel.ImagesUrl = imagesUrlSubmitted
            ? (imagesUrl ?? [])
                .Where(s => !string.IsNullOrWhiteSpace(s) && !string.Equals(s, removeImageUrl, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .Where(existingImagesSet.Contains)
                .ToList()
            : [..existingImages];

        if (settingViewModel.ImagesUrl.Count > 10)
        {
            ModelState.AddModelError(nameof(settingViewModel.Images), "A maximum of 10 images is allowed per plugin.");
            return View(settingViewModel);
        }

        if (settingViewModel.IsPluginPrimaryOwner && (string.IsNullOrEmpty(settingViewModel.Description) || string.IsNullOrEmpty(settingViewModel.PluginTitle)))
        {
            ModelState.AddModelError(nameof(settingViewModel.PluginTitle), "Plugin title and description are required");
            return View(settingViewModel);
        }

        if (settingViewModel.IsPluginPrimaryOwner && !string.IsNullOrWhiteSpace(settingViewModel.PluginTitle))
        {
            var newTitle = settingViewModel.PluginTitle.Trim();
            var currentTitle = existingSetting?.PluginTitle?.Trim();

            if (!string.Equals(newTitle, currentTitle, StringComparison.OrdinalIgnoreCase) && await conn.IsPluginTitleInUse(newTitle, pluginSlug))
            {
                ModelState.AddModelError(nameof(settingViewModel.PluginTitle),
                    "This plugin title is already in use. Please choose a different title.");
                return View(settingViewModel);
            }
        }

        if (settingViewModel.Logo != null)
        {
            if (!settingViewModel.Logo.ValidateUploadedImage(out var errorMessage))
            {
                ModelState.AddModelError(nameof(settingViewModel.Logo), $"Image upload validation failed: {errorMessage}");
                return View(settingViewModel);
            }

            try
            {
                var uniqueBlobName = $"{pluginSlug}-{Guid.NewGuid()}{Path.GetExtension(settingViewModel.Logo.FileName)}";
                settingViewModel.LogoUrl = await azureStorageClient.UploadImageFile(settingViewModel.Logo, uniqueBlobName);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to upload logo for plugin {PluginSlug}", pluginSlug);
                ModelState.AddModelError(nameof(settingViewModel.LogoUrl), "Could not complete settings upload. An error occurred while uploading logo");
                return View(settingViewModel);
            }
        }
        else if (removeLogoFile)
        {
            settingViewModel.Logo = null;
            settingViewModel.LogoUrl = null;
        }

        var uploadedImages = new List<string>();
        var imagesToUpload = (settingViewModel.Images ?? []).OfType<IFormFile>().Where(s => s.Length > 0).ToList();
        if (imagesToUpload.Count > 0)
        {
            if (settingViewModel.ImagesUrl.Count + imagesToUpload.Count > 10)
            {
                ModelState.AddModelError(nameof(settingViewModel.Images), "A maximum of 10 images is allowed per plugin.");
                return View(settingViewModel);
            }
            foreach (var image in imagesToUpload)
            {
                if (!image.ValidateUploadedImage(out var errorMessage))
                {
                    ModelState.AddModelError(nameof(settingViewModel.Images), $"Image upload validation failed: {errorMessage}");
                    return View(settingViewModel);
                }
            }
            try
            {
                uploadedImages = (await Task.WhenAll(imagesToUpload.Select(async image =>
                {
                    var blobName = $"{pluginSlug}-{Guid.NewGuid()}{Path.GetExtension(image.FileName)}";
                    return await azureStorageClient.UploadImageFile(image, blobName);
                }))).ToList();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to upload images for plugin {PluginSlug}", pluginSlug);
                ModelState.AddModelError(nameof(settingViewModel.Images), "Could not complete settings upload. An error occurred while uploading images");
                return View(settingViewModel);
            }
        }

        var existingQueue = new Queue<string>(settingViewModel.ImagesUrl);
        var uploadedQueue = new Queue<string>(uploadedImages);
        var orderedImages = new List<string>(settingViewModel.ImagesUrl.Count + uploadedImages.Count);
        foreach (var marker in imagesOrder ?? [])
        {
            if (string.Equals(marker, "existing", StringComparison.OrdinalIgnoreCase) && existingQueue.Count > 0)
                orderedImages.Add(existingQueue.Dequeue());
            else if (string.Equals(marker, "new", StringComparison.OrdinalIgnoreCase) && uploadedQueue.Count > 0)
                orderedImages.Add(uploadedQueue.Dequeue());
        }
        orderedImages.AddRange(existingQueue);
        orderedImages.AddRange(uploadedQueue);

        if (orderedImages.Count > 10)
        {
            ModelState.AddModelError(nameof(settingViewModel.Images), "A maximum of 10 images is allowed per plugin.");
            settingViewModel.ImagesUrl = orderedImages;
            return View(settingViewModel);
        }

        settingViewModel.ImagesUrl = orderedImages;

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
        if (!await userVerifiedLogic.IsUserEmailVerifiedForPublish(User) || !await userVerifiedLogic.IsUserGithubVerified(User, conn))
        {
            TempData[TempDataConstant.WarningMessage] = "You need to verify your email address and github account in order to create and publish plugins";
            return RedirectToAction("AccountDetails", "Account");
        }

        try
        {
            var identifier = await buildService.FetchIdentifierFromGithubCsprojAsync(model.GitRepository, model.GitRef, model.PluginDirectory);
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
            var existingSetting = await conn.GetSettings(pluginSlug) ?? new PluginSettings();
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
        var pendingRequest = await conn.GetPendingListingRequestForPlugin(pluginSlug);
        var rejectedRequest = await conn.GetLatestRejectedListingRequestForPlugin(pluginSlug);

        model.ReleaseNote = pluginSettings?.Description;
        model.HasPreviousRejection = rejectedRequest != null;

        if (pendingRequest != null)
        {
            model.PendingListing = true;
            model.TelegramVerificationMessage = pendingRequest.TelegramVerificationMessage;
            model.UserReviews = pendingRequest.UserReviews;
            model.ReleaseNote = pendingRequest.ReleaseNote;
            model.AnnouncementDate = pendingRequest.AnnouncementDate;
            var now = DateTimeOffset.UtcNow;
            model.CanSendEmailReminder = now >= pendingRequest.SubmittedAt.AddDays(1);
            TempData[TempDataConstant.WarningMessage] = "Your listing request has been sent and is pending validation";
        }
        else if (rejectedRequest != null)
        {
            // Pre-fill form with data from the latest rejected request
            model.TelegramVerificationMessage = rejectedRequest.TelegramVerificationMessage;
            model.UserReviews = rejectedRequest.UserReviews;
            model.ReleaseNote = rejectedRequest.ReleaseNote;
            model.AnnouncementDate = rejectedRequest.AnnouncementDate;
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

        if (string.IsNullOrWhiteSpace(model.TelegramVerificationMessage) ||
            !Uri.TryCreate(model.TelegramVerificationMessage, UriKind.Absolute, out var telegramUri) ||
            telegramUri.Scheme != Uri.UriSchemeHttps ||
            !telegramUri.Host.Equals("t.me", StringComparison.OrdinalIgnoreCase) ||
            !telegramUri.AbsolutePath.Trim('/').StartsWith("btcpayserver", StringComparison.OrdinalIgnoreCase))
            ModelState.AddModelError(nameof(model.TelegramVerificationMessage),
                "Telegram verification message on BTCPay Server telegram (https://t.me/btcpayserver/... ) channel is required.");
        if (string.IsNullOrWhiteSpace(model.UserReviews))
            ModelState.AddModelError(nameof(model.UserReviews), "User-reviews link is required and must be an HTTPS URL.");

        if (!ModelState.IsValid)
        {
            var owners = await conn.GetPluginOwners(pluginSlug);
            model.PendingListing = await conn.HasPendingListingRequest(pluginSlug);
            model = await ListingRequirementsMet(conn, pluginSettings, owners, model);
            return View(model);
        }

        await conn.CreateListingRequest(pluginSlug, model.ReleaseNote.Trim(), model.TelegramVerificationMessage.Trim(), model.UserReviews.Trim(),
            model.AnnouncementDate);
        await SendRequestListingEmail(conn, pluginSlug.ToString());
        TempData[TempDataConstant.SuccessMessage] = "Your listing request has been sent and is pending validation";
        return RedirectToAction(nameof(Dashboard), new { pluginSlug });
    }

    private async Task<RequestListingViewModel> ListingRequirementsMet(NpgsqlConnection conn, PluginSettings pluginSettings, List<OwnerVm> owners,
        RequestListingViewModel model)
    {
        if (pluginSettings == null || owners == null || owners.Count == 0)
        {
            model.Step = RequestListingViewModel.State.Invalid;
            return model;
        }

        var docsMissing = string.IsNullOrWhiteSpace(pluginSettings?.GitRepository) || string.IsNullOrWhiteSpace(pluginSettings?.Documentation)
                                                                                        || string.IsNullOrWhiteSpace(pluginSettings?.Logo)
                                                                                        || string.IsNullOrWhiteSpace(pluginSettings?.Description)
                                                                                        || string.IsNullOrWhiteSpace(pluginSettings?.VideoUrl);

        var ownerNotVerified = false;
        foreach (var owner in owners)
            if (!await conn.IsSocialAccountsVerified(owner.UserId))
            {
                ownerNotVerified = true;
                break;
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

        var request = await conn.GetPendingListingRequestForPlugin(pluginSlug);
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
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Release(
        [ModelBinder(typeof(PluginSlugModelBinder))]
        PluginSlug pluginSlug,
        [ModelBinder(typeof(PluginVersionModelBinder))]
        PluginVersion version, string command, IFormFile? signatureFile)
    {
        switch (command)
        {
            case "remove":
                var removeResult = await versionLifecycleService.RemoveAsync(pluginSlug, version);
                if (!removeResult.Success)
                    return HandleVersionLifecycleFailure(pluginSlug, version, removeResult);

                TempData[TempDataConstant.SuccessMessage] = $"Version {version} removed";
                return RedirectToAction(nameof(Build), new { pluginSlug = pluginSlug.ToString(), buildId = removeResult.BuildId!.Value });

            case "sign_release":
                if (signatureFile is not { Length: > 0 })
                {
                    TempData[TempDataConstant.WarningMessage] = "Please upload a valid GPG signature file (.asc or .sig)";
                    return RedirectToAction(nameof(Version), new { pluginSlug = pluginSlug.ToString(), version = version.ToString() });
                }

                var signatureBytes = await ReadFormFileBytes(signatureFile);
                var signReleaseResult = await versionLifecycleService.ReleaseAsync(pluginSlug, version, userManager.GetUserId(User)!, signatureBytes);
                if (!signReleaseResult.Success)
                    return HandleVersionLifecycleFailure(pluginSlug, version, signReleaseResult);

                TempData[TempDataConstant.SuccessMessage] = $"Version {version} released";
                return RedirectToAction(nameof(Version), new { pluginSlug = pluginSlug.ToString(), version = version.ToString() });

            case "unrelease":
                var unreleaseResult = await versionLifecycleService.UnreleaseAsync(pluginSlug, version);
                if (!unreleaseResult.Success)
                    return HandleVersionLifecycleFailure(pluginSlug, version, unreleaseResult);

                TempData[TempDataConstant.SuccessMessage] = $"Version {version} unreleased";
                return RedirectToAction(nameof(Version), new { pluginSlug = pluginSlug.ToString(), version = version.ToString() });

            default:
                var releaseResult = await versionLifecycleService.ReleaseAsync(pluginSlug, version, userManager.GetUserId(User)!, null);
                if (!releaseResult.Success)
                    return HandleVersionLifecycleFailure(pluginSlug, version, releaseResult);

                TempData[TempDataConstant.SuccessMessage] = $"Version {version} released";
                return RedirectToAction(nameof(Version), new { pluginSlug = pluginSlug.ToString(), version = version.ToString() });
        }
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
        long buildId,
        bool openCompatibilityModal = false)
    {
        await using var conn = await connectionFactory.Open();
        var row =
            await conn
                .QueryFirstOrDefaultAsync<(string manifest_info, string build_info, string state, DateTimeOffset created_at, bool published, bool pre_release,
                    string signatureproof, int[]? btcpay_min_ver, bool btcpay_min_ver_override_enabled,
                    int[]? btcpay_max_ver, bool btcpay_max_ver_override_enabled)>(
                    "SELECT manifest_info, build_info, state, created_at, v.ver IS NOT NULL, v.pre_release, v.signatureproof, v.btcpay_min_ver, " +
                    "v.btcpay_min_ver_override_enabled AS btcpay_min_ver_override_enabled, " +
                    "v.btcpay_max_ver, " +
                    "v.btcpay_max_ver_override_enabled AS btcpay_max_ver_override_enabled FROM builds b " +
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
        vm.ManifestInfo = ManifestHelper.NiceJson(row.manifest_info, signatureProof?.Fingerprint);
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
        vm.ManifestInfoSha256Hash = ManifestHelper.GetManifestHash(ManifestHelper.NiceJson(row.manifest_info), vm.RequireGPGSignatureForRelease);
        vm.Published = row.published;
        var effectiveManifestMinVersion = (manifest?.BTCPayMinVersion ?? PluginVersion.Zero).ToString();
        var effectiveManifestMaxVersion = manifest?.BTCPayMaxVersion?.ToString();
        vm.BTCPayMinVersion = row.btcpay_min_ver is { Length: > 0 } ? string.Join('.', row.btcpay_min_ver) : effectiveManifestMinVersion;
        vm.BTCPayMaxVersion = row.btcpay_max_ver is { Length: > 0 } ? string.Join('.', row.btcpay_max_ver) : effectiveManifestMaxVersion;
        vm.HasBTCPayCompatibilityOverride = row.btcpay_min_ver_override_enabled || row.btcpay_max_ver_override_enabled;
        vm.CanEditBTCPayCompatibility = vm.Version is not null;
        vm.OpenBTCPayCompatibilityModal = openCompatibilityModal;
        if (logs != "")
            vm.Logs = logs;
        return View(vm);
    }

    [HttpPost("builds/{buildId}/btcpay-compatibility")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateVersionBTCPayCompatibility(
        [ModelBinder(typeof(PluginSlugModelBinder))]
        PluginSlug pluginSlug,
        long buildId,
        [FromForm] string? btcpayMinVersion,
        [FromForm] string? btcpayMaxVersion,
        [FromForm] bool useManifestDefaults = false)
    {
        await using var conn = await connectionFactory.Open();
        var manifestInfo = await conn.QueryFirstOrDefaultAsync<string?>(
            """
            SELECT b.manifest_info
            FROM versions v
            JOIN builds b ON b.plugin_slug = v.plugin_slug AND b.id = v.build_id
            WHERE v.plugin_slug = @pluginSlug AND v.build_id = @buildId
            LIMIT 1
            """,
            new { pluginSlug = pluginSlug.ToString(), buildId });
        if (manifestInfo is null)
            return NotFound();
        var strictManifestParsed = PluginManifest.TryParse(manifestInfo, out var strictManifest, strictBTCPayVersionCondition: true);
        if (useManifestDefaults && !strictManifestParsed)
        {
            TempData[TempDataConstant.WarningMessage] = "Cannot reset compatibility because the manifest BTCPayServer condition is unsupported.";
            return RedirectToAction(nameof(Build), new { pluginSlug = pluginSlug.ToString(), buildId, openCompatibilityModal = true });
        }

        var manifest = strictManifestParsed ? strictManifest : PluginManifest.Parse(manifestInfo);

        var manifestMinVersion = manifest.BTCPayMinVersion ?? PluginVersion.Zero;
        var manifestMaxVersion = manifest.BTCPayMaxVersion;
        var strictManifestMinVersion = strictManifestParsed ? strictManifest.BTCPayMinVersion ?? PluginVersion.Zero : null;
        var strictManifestMaxVersion = strictManifestParsed ? strictManifest.BTCPayMaxVersion : null;

        PluginVersion candidateMinVersion;
        PluginVersion? candidateMaxVersion;

        if (useManifestDefaults)
        {
            candidateMinVersion = manifestMinVersion;
            candidateMaxVersion = manifestMaxVersion;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(btcpayMinVersion) || !PluginVersion.TryParse(btcpayMinVersion.Trim(), out candidateMinVersion))
            {
                TempData[TempDataConstant.WarningMessage] = "BTCPay min version must be a valid version number.";
                return RedirectToAction(nameof(Build), new { pluginSlug = pluginSlug.ToString(), buildId, openCompatibilityModal = true });
            }

            if (!string.IsNullOrWhiteSpace(btcpayMaxVersion))
            {
                if (!PluginVersion.TryParse(btcpayMaxVersion.Trim(), out candidateMaxVersion))
                {
                    TempData[TempDataConstant.WarningMessage] = "BTCPay max version must be a valid version number.";
                    return RedirectToAction(nameof(Build), new { pluginSlug = pluginSlug.ToString(), buildId, openCompatibilityModal = true });
                }
            }
            else
                candidateMaxVersion = null;

        }

        if (candidateMaxVersion is not null && candidateMinVersion.CompareTo(candidateMaxVersion) > 0)
        {
            TempData[TempDataConstant.WarningMessage] = $"BTCPay max version must be greater than or equal to the minimum version {candidateMinVersion}.";
            return RedirectToAction(nameof(Build), new { pluginSlug = pluginSlug.ToString(), buildId, openCompatibilityModal = true });
        }

        var clearMinOverride = useManifestDefaults || (strictManifestParsed && VersionEquals(candidateMinVersion, strictManifestMinVersion));
        var clearMaxOverride = useManifestDefaults || (strictManifestParsed && VersionEquals(candidateMaxVersion, strictManifestMaxVersion));

        await conn.ExecuteAsync(
            """
            UPDATE versions
            SET btcpay_min_ver = @btcpayMinVersion,
                btcpay_min_ver_override_enabled = @minOverrideEnabled,
                btcpay_max_ver = @btcpayMaxVersion,
                btcpay_max_ver_override_enabled = @maxOverrideEnabled
            WHERE plugin_slug = @pluginSlug AND build_id = @buildId
            """,
            new
            {
                pluginSlug = pluginSlug.ToString(),
                buildId,
                btcpayMinVersion = clearMinOverride ? manifestMinVersion.VersionParts : candidateMinVersion.VersionParts,
                minOverrideEnabled = !clearMinOverride,
                btcpayMaxVersion = clearMaxOverride ? manifestMaxVersion?.VersionParts : candidateMaxVersion?.VersionParts,
                maxOverrideEnabled = !clearMaxOverride
            });

        await outputCacheStore.EvictByTagAsync(CacheTags.Plugins, CancellationToken.None);
        TempData[TempDataConstant.SuccessMessage] = clearMinOverride && clearMaxOverride
            ? "Compatibility reset to the manifest defaults."
            : "Compatibility updated successfully.";
        return RedirectToAction(nameof(Build), new { pluginSlug = pluginSlug.ToString(), buildId });
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
            else if (repo.StartsWith("https://github.com/"))
                repoName = repo.Substring("https://github.com/".Length);
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

    private static bool VersionEquals(PluginVersion? left, PluginVersion? right)
    {
        if (left is null && right is null)
            return true;
        if (left is null || right is null)
            return false;
        return left.CompareTo(right) == 0;
    }

    private IActionResult HandleVersionLifecycleFailure(PluginSlug pluginSlug, PluginVersion version, VersionLifecycleResult result)
    {
        if (result.FailureCode == VersionLifecycleFailureCode.NotFound)
            return NotFound();

        TempData[TempDataConstant.WarningMessage] = result.Message ?? "Version lifecycle operation failed";
        return RedirectToAction(nameof(Version), new { pluginSlug = pluginSlug.ToString(), version = version.ToString() });
    }

    private static async Task<byte[]> ReadFormFileBytes(IFormFile file)
    {
        using var ms = new MemoryStream((int)file.Length);
        await file.CopyToAsync(ms);
        return ms.ToArray();
    }

    [HttpGet("owners")]
    public async Task<IActionResult> Owners([ModelBinder(typeof(PluginSlugModelBinder))] PluginSlug pluginSlug)
    {
        var currentUserId = userManager.GetUserId(User) ?? throw new InvalidOperationException();

        await using var conn = await connectionFactory.Open();

        var owners = await conn.GetPluginOwners(pluginSlug);

        var vm = new PluginOwnersPageViewModel
        {
            PluginSlug = pluginSlug.ToString(),
            CurrentUserId = currentUserId,
            IsPrimaryOwner = owners.Any(o => o.UserId == currentUserId && o.IsPrimary),
            Owners = owners
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

            var result = await ownershipService.AddOwnerByEmailAsync(pluginSlug, email);

            if (!result.Success)
            {
                TempData[TempDataConstant.WarningMessage] = result.Error;
                return RedirectToAction(nameof(Owners), new { pluginSlug });
            }

            TempData[TempDataConstant.SuccessMessage] = "User added.";
            return RedirectToAction(nameof(Owners), new { pluginSlug });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error while adding owner");
            TempData[TempDataConstant.WarningMessage] = "Unexpected error while adding owner";
            return RedirectToAction(nameof(Owners), new { pluginSlug });
        }
    }

    [HttpPost("owners/{userId}/transfer-primary-owner")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TransferPrimaryOwner(
        [ModelBinder(typeof(PluginSlugModelBinder))]
        PluginSlug pluginSlug,
        string userId)
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

            var result = await ownershipService.TransferPrimaryAsync(pluginSlug, userId);

            if (!result.Success)
            {
                TempData[TempDataConstant.WarningMessage] = result.Error;
                return RedirectToAction(nameof(Owners), new { pluginSlug });
            }

            TempData[TempDataConstant.SuccessMessage] = "Primary owner transferred.";
            return RedirectToAction(nameof(Owners), new { pluginSlug });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error while transferring primary owner");
            TempData[TempDataConstant.WarningMessage] = "Unexpected error while transferring primary owner";
            return RedirectToAction(nameof(Owners), new { pluginSlug });
        }
    }

    [HttpPost("owners/{userId}/remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveOwner(
        [ModelBinder(typeof(PluginSlugModelBinder))]
        PluginSlug pluginSlug,
        string userId)
    {
        try
        {
            var currentUserId = userManager.GetUserId(User);

            var result = await ownershipService.RemoveOwnerAsync(
                pluginSlug,
                userId,
                currentUserId,
                false);

            if (!result.Success)
            {
                TempData[TempDataConstant.WarningMessage] = result.Error;
                return RedirectToAction(nameof(Owners), new { pluginSlug });
            }

            TempData[TempDataConstant.SuccessMessage] = "Owner removed.";
            return RedirectToAction(nameof(Owners), new { pluginSlug });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error while removing owner");
            TempData[TempDataConstant.WarningMessage] = "Unexpected error while removing owner";
            return RedirectToAction(nameof(Owners), new { pluginSlug });
        }
    }
}
