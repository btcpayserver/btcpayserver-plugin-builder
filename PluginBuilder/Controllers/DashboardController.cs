using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using PluginBuilder.ViewModels.Plugin;

namespace PluginBuilder.Controllers;

[Authorize]
public class DashboardController(
    DBConnectionFactory connectionFactory,
    UserManager<IdentityUser> userManager,
    EmailVerifiedLogic emailVerifiedLogic) : Controller
{
    // plugin methods

    [HttpGet("/plugins/create")]
    public async Task<IActionResult> CreatePlugin()
    {
        await using var conn = await connectionFactory.Open();
        if (!await emailVerifiedLogic.IsUserEmailVerified(User))
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
        if (!await emailVerifiedLogic.IsUserEmailVerified(User))
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
}
