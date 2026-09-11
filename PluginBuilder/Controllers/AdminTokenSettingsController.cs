using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PluginBuilder.Services;
using PluginBuilder.Util;

namespace PluginBuilder.Controllers;

[Authorize(Roles = Roles.ServerAdmin)]
[AutoValidateAntiforgeryToken]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("account/admin-access-tokens")]
public class AdminTokenSettingsController(AdminAccessTokenService tokens) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index() => View(new TokenPage { Tokens = await tokens.List(User.FindFirstValue(ClaimTypes.NameIdentifier)!) });

    [HttpPost]
    public async Task<IActionResult> Create(TokenPage model)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        if (ModelState.IsValid)
            model.Issued = await tokens.Create(userId, model.Creation.Name, model.Creation.ExpiresInDays);
        model.Tokens = await tokens.List(userId);
        return View("Index", model);
    }

    [HttpPost("{id:guid}/revoke")]
    public async Task<IActionResult> Revoke(Guid id)
    {
        if (!await tokens.Revoke(User.FindFirstValue(ClaimTypes.NameIdentifier)!, id))
            return NotFound();
        TempData[TempDataConstant.SuccessMessage] = "Token revoked. It can no longer make new requests.";
        return RedirectToAction(nameof(Index));
    }

    public class TokenPage
    {
        public AdminAccessTokensController.CreateRequest Creation { get; set; } = new();
        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public IReadOnlyList<AdminAccessTokenService.TokenInfo> Tokens { get; set; } = [];
        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public AdminAccessTokenService.IssuedToken? Issued { get; set; }
    }
}
