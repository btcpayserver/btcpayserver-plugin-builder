using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.Services;
using PluginBuilder.Util;
using PluginBuilder.Util.Extensions;
using PluginBuilder.ViewModels.Plugin;

namespace PluginBuilder.Controllers;

[Authorize]
public class DashboardController(
    DBConnectionFactory connectionFactory,
    UserManager<IdentityUser> userManager,
    AzureStorageClient azureStorageClient,
    UserVerifiedLogic userVerifiedLogic,
    ILogger<DashboardController> logger) : Controller
{
    private static readonly TimeSpan _storageTimeout = TimeSpan.FromSeconds(10);

    // plugin methods

    [HttpGet("/plugins/create")]
    public async Task<IActionResult> CreatePlugin()
    {
        if (!await userVerifiedLogic.IsUserEmailVerifiedForPublish(User))
        {
            TempData[TempDataConstant.WarningMessage] = "You need to verify your email address in order to create and publish plugins";
            return RedirectToAction(nameof(AccountController.AccountDetails), "Account");
        }

        await using var conn = await connectionFactory.Open();
        if (!await userVerifiedLogic.IsUserGithubVerified(User, conn))
        {
            TempData[TempDataConstant.WarningMessage] = "You need to verify your GitHub account in order to create and publish plugins";
            return RedirectToAction(nameof(AccountController.AccountDetails), "Account");
        }

        return View();
    }

    [HttpPost("/plugins/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreatePlugin(
        CreatePluginViewModel model,
        [FromForm] List<string>? imagesOrder = null)
    {
        const long maxTotalBytes = 10 * 1024 * 1024;

        if (!ModelState.IsValid)
            return View(model);

        var totalUploadBytes = Request.Form.Files.Sum(file => file.Length);
        if (totalUploadBytes > maxTotalBytes)
        {
            ModelState.AddModelError(nameof(model.Images), "Total size of uploaded files cannot exceed 10MB.");
            return View(model);
        }

        if (!PluginSlug.TryParse(model.PluginSlug, out var pluginSlug))
        {
            ModelState.AddModelError(nameof(model.PluginSlug),
                "Invalid plugin slug; it should only contain lowercase Latin letters, numbers, or '-' (example: my-awesome-plugin)");
            return View(model);
        }

        await using var conn = await connectionFactory.Open();
        if (!await userVerifiedLogic.IsUserEmailVerifiedForPublish(User))
        {
            TempData[TempDataConstant.WarningMessage] = "You need to verify your email address in order to create and publish plugins";
            return RedirectToAction("AccountDetails", "Account");
        }

        var userId = userManager.GetUserId(User)!;
        if (!await userVerifiedLogic.IsUserGithubVerified(User, conn))
        {
            TempData[TempDataConstant.WarningMessage] = "You need to verify your GitHub Account in order to create and publish plugins";
            return RedirectToAction("AccountDetails", "Account");
        }

        if (await conn.IsPluginTitleInUse(model.PluginTitle))
        {
            ModelState.AddModelError(nameof(model.PluginTitle),
                "This plugin title is already in use. Please choose a different title.");
            return View(model);
        }

        if (!string.IsNullOrEmpty(model.VideoUrl))
        {
            if (!Uri.TryCreate(model.VideoUrl, UriKind.Absolute, out var videoUri) || videoUri.Scheme != Uri.UriSchemeHttps)
            {
                ModelState.AddModelError(nameof(model.VideoUrl), "Video URL must be a valid HTTPS URL.");
                return View(model);
            }

            if (!model.VideoUrl.IsSupportedVideoUrl())
            {
                ModelState.AddModelError(nameof(model.VideoUrl), "Video URL must be from a supported platform (YouTube, Vimeo).");
                return View(model);
            }
        }

        if (model.Logo != null)
        {
            string errorMessage;
            if (!model.Logo.ValidateUploadedImage(out errorMessage))
            {
                ModelState.AddModelError(nameof(model.Logo), $"Image upload validation failed: {errorMessage}");
                return View(model);
            }
        }

        model.ImagesUrl = [];
        var imagesToUpload = (model.Images ?? []).Where(s => s is { Length: > 0 }).ToList();
        if (imagesToUpload.Count > 0)
        {
            if (imagesToUpload.Count > 10)
            {
                ModelState.AddModelError(nameof(model.Images), "A maximum of 10 images is allowed per plugin.");
                return View(model);
            }
            foreach (var image in imagesToUpload)
            {
                if (image.ValidateUploadedImage(out var errorMessage))
                    continue;
                ModelState.AddModelError(nameof(model.Images), $"Image upload validation failed: {errorMessage}");
                return View(model);
            }
        }

        if (!await conn.NewPlugin(pluginSlug, userId))
        {
            ModelState.AddModelError(nameof(model.PluginSlug), "This slug already exists");
            return View(model);
        }

        var baseSettings = new PluginSettings
        {
            PluginTitle = model.PluginTitle,
            Description = model.Description,
            VideoUrl = model.VideoUrl,
            Logo = null,
            Images = []
        };
        if (!await conn.SetPluginSettings(pluginSlug, baseSettings))
        {
            await conn.DeletePlugin(pluginSlug);
            ModelState.AddModelError(string.Empty, "Could not complete plugin creation.");
            return View(model);
        }

        if (model.Logo == null && imagesToUpload.Count == 0)
            return RedirectToAction(nameof(PluginController.Dashboard), "Plugin", new { pluginSlug = pluginSlug.ToString() });

        string? logoUrl = null;
        var uploadedImages = new List<string>();
        var uploadedBlobNames = new List<string>();
        var mediaUploadFailed = false;

        if (model.Logo != null)
            try
            {
                var uniqueBlobName = $"{pluginSlug}-{Guid.NewGuid()}{Path.GetExtension(model.Logo.FileName)}";
                logoUrl = await azureStorageClient.UploadImageFile(model.Logo, uniqueBlobName, _storageTimeout);
                uploadedBlobNames.Add(uniqueBlobName);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to upload logo during create flow for plugin {PluginSlug}", pluginSlug);
                mediaUploadFailed = true;
            }

        foreach (var image in imagesToUpload)
            try
            {
                var blobName = $"{pluginSlug}-{Guid.NewGuid()}{Path.GetExtension(image.FileName)}";
                uploadedImages.Add(await azureStorageClient.UploadImageFile(image, blobName, _storageTimeout));
                uploadedBlobNames.Add(blobName);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to upload plugin image during create flow for plugin {PluginSlug}", pluginSlug);
                mediaUploadFailed = true;
            }

        if (logoUrl != null || uploadedImages.Count > 0)
        {
            var uploadedQueue = new Queue<string>(uploadedImages);
            var orderedImages = new List<string>(uploadedImages.Count);
            foreach (var marker in imagesOrder ?? [])
            {
                if (string.Equals(marker, "new", StringComparison.OrdinalIgnoreCase) && uploadedQueue.Count > 0)
                    orderedImages.Add(uploadedQueue.Dequeue());
            }

            orderedImages.AddRange(uploadedQueue);
            var mediaSettings = new PluginSettings
            {
                PluginTitle = model.PluginTitle,
                Description = model.Description,
                VideoUrl = model.VideoUrl,
                Logo = logoUrl,
                Images = orderedImages
            };

            if (!await conn.SetPluginSettings(pluginSlug, mediaSettings))
            {
                logger.LogWarning("Failed to persist optional media during create flow for plugin {PluginSlug}", pluginSlug);
                foreach (var blobName in uploadedBlobNames)
                    try
                    {
                        await azureStorageClient.DeleteImageFileIfExists(blobName, _storageTimeout);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to clean up uploaded blob {BlobName} for plugin {PluginSlug}", blobName, pluginSlug);
                    }

                TempData[TempDataConstant.WarningMessage] =
                    "Plugin created, but logo/images could not be saved. You can add them later in Settings.";
                return RedirectToAction(nameof(PluginController.Settings), "Plugin", new { pluginSlug = pluginSlug.ToString() });
            }
        }

        if (!mediaUploadFailed)
            return RedirectToAction(nameof(PluginController.Dashboard), "Plugin", new { pluginSlug = pluginSlug.ToString() });

        TempData[TempDataConstant.WarningMessage] =
            "Plugin created, but some media could not be saved. You can add them later in Settings.";
        return RedirectToAction(nameof(PluginController.Settings), "Plugin", new { pluginSlug = pluginSlug.ToString() });
    }
}
