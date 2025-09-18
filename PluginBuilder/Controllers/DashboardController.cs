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
    EmailVerifiedLogic emailVerifiedLogic) : Controller
{
    // plugin methods

    [HttpGet("/plugins/create")]
    public async Task<IActionResult> CreatePlugin()
    {
        await using var conn = await connectionFactory.Open();
        if (!await emailVerifiedLogic.IsUserEmailVerifiedForPublish(User))
        {
            TempData[TempDataConstant.WarningMessage] =
                "You need to verify your email address in order to create and publish plugins";
            return RedirectToAction("AccountDetails", "Account");
        }

        return View();
    }

    [HttpPost("/plugins/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreatePlugin(CreatePluginViewModel model)
    {
        if (!PluginSlug.TryParse(model.PluginSlug, out var pluginSlug))
        {
            ModelState.AddModelError(nameof(model.PluginSlug),
                "Invalid plug slug, it should only contains latin letter in lowercase or numbers or '-' (example: my-awesome-plugin)");
            return View(model);
        }

        await using var conn = await connectionFactory.Open();
        if (!await emailVerifiedLogic.IsUserEmailVerifiedForPublish(User))
        {
            TempData[TempDataConstant.WarningMessage] =
                "You need to verify your email address in order to create and publish plugins";
            return RedirectToAction("AccountDetails", "Account");
        }

        var userId = userManager.GetUserId(User)!;

        if (!await conn.IsGithubAccountVerified(userId))
        {
            TempData[TempDataConstant.WarningMessage] =
                "You need to verify your Github Account in order to create and publish plugins";
            return RedirectToAction("AccountDetails", "Account");
        }

        if (!await conn.NewPlugin(pluginSlug))
        {
            ModelState.AddModelError(nameof(model.PluginSlug), "This slug already exists");
            return View(model);
        }

        if (model.Logo != null)
        {
            string errorMessage;
            if (!model.Logo.ValidateUploadedImage(out errorMessage))
            {
                ModelState.AddModelError(nameof(model.Logo), $"Image upload validation failed: {errorMessage}");
                return View(model);
            }
            try
            {
                model.LogoUrl = await azureStorageClient.UploadImageFile(model.Logo, $"{model.Logo.FileName}");
            }
            catch (Exception)
            {
                ModelState.AddModelError(nameof(model.Logo), "Could not complete plugin creation. An error occurred while uploading logo image");
                return View(model);
            }
        }
        await conn.AddUserPlugin(pluginSlug, userId);
        await conn.AssignPluginPrimaryOwner(pluginSlug, userId);
        await conn.SetPluginSettings(pluginSlug, new PluginSettings { Logo = model.LogoUrl });
        return RedirectToAction(nameof(PluginController.Dashboard), "Plugin", new { pluginSlug = pluginSlug.ToString() });
    }
}
