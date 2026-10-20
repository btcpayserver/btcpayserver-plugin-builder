using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PluginBuilder.Authentication;
using PluginBuilder.Services;
using PluginBuilder.Util;

namespace PluginBuilder.Controllers;

[ApiController]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/v1/admin/access-tokens")]
// A delegated token cannot mint replacement credentials or revoke other tokens.
[Authorize(Roles = Roles.ServerAdmin, AuthenticationSchemes = PluginBuilderAuthenticationSchemes.BasicAuth)]
public class AdminAccessTokensController(AdminAccessTokenService tokens, IOptions<IdentityOptions> identityOptions) : ControllerBase
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    private string? SecurityStamp => User.FindFirstValue(identityOptions.Value.ClaimsIdentity.SecurityStampClaimType);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok(await tokens.List(UserId, cancellationToken));

    // 401 when the credentials changed while the request was running.
    [HttpPost]
    public async Task<IActionResult> Create(CreateRequest request, CancellationToken cancellationToken) =>
        await tokens.Create(UserId, request.Name, request.ExpiresInDays, SecurityStamp, cancellationToken) is { } issued
            ? StatusCode(201, issued)
            : Unauthorized();

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken cancellationToken) =>
        await tokens.Revoke(UserId, id, SecurityStamp, cancellationToken) switch
        {
            true => NoContent(),
            false => NotFound(),
            null => Unauthorized()
        };

    public sealed class CreateRequest
    {
        [Required, MaxLength(100)] public string Name { get; set; } = "";
        [Range(1, 365)] public int ExpiresInDays { get; set; } = 90;
    }
}
