using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PluginBuilder.Services;
using PluginBuilder.Util;

namespace PluginBuilder.Controllers;

[Authorize(Roles = Roles.ServerAdmin)]
[AutoValidateAntiforgeryToken]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("account/admin-access-tokens")]
public class AdminTokenSettingsController(AdminAccessTokenService tokens, SignInManager<IdentityUser> signInManager,
    IOptions<IdentityOptions> identityOptions) : Controller
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    // Cookie stamp validation is periodic; token changes check the session's stamp immediately.
    private string? SecurityStamp => User.FindFirstValue(identityOptions.Value.ClaimsIdentity.SecurityStampClaimType);

    [HttpGet]
    public async Task<IActionResult> Index() => View(new TokenPage { Tokens = await tokens.List(UserId) });

    [HttpPost]
    public async Task<IActionResult> Create(TokenPage model)
    {
        if (ModelState.IsValid)
        {
            model.Issued = await tokens.Create(UserId, model.Creation.Name, model.Creation.ExpiresInDays, SecurityStamp);
            if (model.Issued is null)
                return await SignOutStaleSession();
        }
        model.Tokens = await tokens.List(UserId);
        return View("Index", model);
    }

    [HttpPost("{id:guid}/revoke")]
    public async Task<IActionResult> Revoke(Guid id)
    {
        switch (await tokens.Revoke(UserId, id, SecurityStamp))
        {
            case null:
                return await SignOutStaleSession();
            case false:
                return NotFound();
        }
        TempData[TempDataConstant.SuccessMessage] = "Token revoked. It can no longer make new requests.";
        return RedirectToAction(nameof(Index));
    }

    private async Task<IActionResult> SignOutStaleSession()
    {
        await signInManager.SignOutAsync();
        return Challenge();
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
