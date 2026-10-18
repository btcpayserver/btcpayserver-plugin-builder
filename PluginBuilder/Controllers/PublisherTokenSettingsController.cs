using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using PluginBuilder.Services;
using PluginBuilder.Util;

namespace PluginBuilder.Controllers;

// Personal API tokens for publishers. One page in account settings lists every token across the user's plugins, so all
// API credentials can be seen and revoked in one place. Tokens cover a chosen plugin or all currently owned plugins.
[Authorize]
[AutoValidateAntiforgeryToken]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("account/api-tokens")]
public class PublisherTokenSettingsController(PublisherAccessTokenService tokens, SignInManager<IdentityUser> signInManager) : Controller
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    [HttpGet]
    public async Task<IActionResult> Index() => View(await Load(new TokenPage()));

    [HttpPost]
    public async Task<IActionResult> Create(TokenPage model)
    {
        // Cookie stamp validation is periodic; token issuance must reject a stale session immediately.
        var user = await signInManager.ValidateSecurityStampAsync(User);
        if (user is null)
        {
            await signInManager.SignOutAsync();
            return Challenge();
        }

        if (ModelState.IsValid)
        {
            try
            {
                model.Issued = await tokens.Create(UserId, model.Creation.PluginSlug, model.Creation.Name,
                    model.Creation.ExpiresInDays == 0 ? null : model.Creation.ExpiresInDays, user.SecurityStamp!);
            }
            catch (ArgumentException ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
            }
        }
        return View("Index", await Load(model));
    }

    [HttpPost("{id:guid}/revoke")]
    public async Task<IActionResult> Revoke(Guid id)
    {
        if (await signInManager.ValidateSecurityStampAsync(User) is null)
        {
            await signInManager.SignOutAsync();
            return Challenge();
        }

        if (!await tokens.Revoke(UserId, id))
            return NotFound();
        TempData[TempDataConstant.SuccessMessage] = "Token revoked. It can no longer make requests.";
        return RedirectToAction(nameof(Index));
    }

    private async Task<TokenPage> Load(TokenPage model)
    {
        model.Tokens = await tokens.List(UserId);
        model.Plugins = await tokens.OwnedPlugins(UserId);
        return model;
    }

    public class TokenPage
    {
        public CreateRequest Creation { get; set; } = new();
        [BindNever] public IReadOnlyList<PublisherAccessTokenService.TokenInfo> Tokens { get; set; } = [];
        [BindNever] public IReadOnlyList<string> Plugins { get; set; } = [];
        [BindNever] public PublisherAccessTokenService.IssuedToken? Issued { get; set; }
    }

    public class CreateRequest
    {
        [Required, MaxLength(100)] public string Name { get; set; } = "";
        [Required] public string PluginSlug { get; set; } = "";
        // 0 = no expiry; otherwise 1-365 days.
        [Range(0, 365)] public int ExpiresInDays { get; set; } = 90;
    }
}
