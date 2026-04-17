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
    UserVerifiedLogic userVerifiedLogic) : Controller
{

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
    public async Task<IActionResult> CreatePlugin(CreatePluginViewModel model)
    {
        if (!ModelState.IsValid)
            return View(model);

        if (!PluginSlug.TryParse(model.PluginSlug, out var pluginSlug))
        {
            ModelState.AddModelError(nameof(model.PluginSlug),
                "Invalid plugin slug; it should only contain lowercase Latin letters, numbers, or '-' (example: my-awesome-plugin)");
            return View(model);
        }

        if (!string.IsNullOrEmpty(model.VideoUrl))
        {
            if (!Uri.TryCreate(model.VideoUrl, UriKind.Absolute, out var videoUri) || videoUri.Scheme != Uri.UriSchemeHttps)
            {
                ModelState.AddModelError(nameof(model.VideoUrl), "Video URL must be a valid HTTPS URL");
                return View(model);
            }
            if (!model.VideoUrl.IsSupportedVideoUrl())
            {
                ModelState.AddModelError(nameof(model.VideoUrl), "Video URL must be from a supported platform (YouTube, Vimeo)");
                return View(model);
            }
        }

        if (model.Logo != null && !model.Logo.ValidateImageFile(out var logoError))
        {
            ModelState.AddModelError(nameof(model.Logo), $"Image upload validation failed: {logoError}");
            return View(model);
        }

        if (!await userVerifiedLogic.IsUserEmailVerifiedForPublish(User))
        {
            TempData[TempDataConstant.WarningMessage] = "You need to verify your email address in order to create and publish plugins";
            return RedirectToAction("AccountDetails", "Account");
        }

        await using var conn = await connectionFactory.Open();
        var userId = userManager.GetUserId(User)!;
        if (!await userVerifiedLogic.IsUserGithubVerified(User, conn))
        {
            TempData[TempDataConstant.WarningMessage] = "You need to verify your GitHub Account in order to create and publish plugins";
            return RedirectToAction("AccountDetails", "Account");
        }

        if (await conn.IsPluginTitleInUse(model.PluginTitle))
        {
            ModelState.AddModelError(nameof(model.PluginTitle), "This plugin title is already in use. Please choose a different title.");
            return View(model);
        }

        if (!await conn.NewPlugin(pluginSlug, userId))
        {
            ModelState.AddModelError(nameof(model.PluginSlug), "This slug already exists");
            return View(model);
        }

        string? logoUrl = null;
        if (model.Logo != null)
        {
            try
            {
                var uniqueBlobName = $"{pluginSlug}-{Guid.NewGuid()}{Path.GetExtension(model.Logo.FileName)}";
                logoUrl = await azureStorageClient.UploadImageFile(model.Logo, uniqueBlobName);
            }
            catch (Exception) { }
        }

        var baseSettings = new PluginSettings
        {
            PluginTitle = model.PluginTitle,
            Description = model.Description,
            VideoUrl = model.VideoUrl,
            Logo = logoUrl,
            Images = []
        };

        if (!await conn.SetPluginSettings(pluginSlug, baseSettings))
        {
            await conn.DeletePlugin(pluginSlug);
            if (logoUrl is not null)
            {
                var uploadedBlobName = Path.GetFileName(new Uri(logoUrl).AbsolutePath);
                try
                {
                    await azureStorageClient.DeleteImageFileIfExists(uploadedBlobName);
                }
                catch { }
            }
            ModelState.AddModelError(string.Empty, "Could not complete plugin creation.");
            return View(model);
        }

        TempData[TempDataConstant.SuccessMessage] = "Plugin created successfully.";
        return RedirectToAction(nameof(PluginController.Dashboard), "Plugin", new { pluginSlug = pluginSlug.ToString() });
    }
}
