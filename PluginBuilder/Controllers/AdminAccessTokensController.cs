using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PluginBuilder.Authentication;
using PluginBuilder.Services;
using PluginBuilder.Util;

namespace PluginBuilder.Controllers;

[ApiController]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/v1/admin/access-tokens")]
// A delegated token cannot mint replacement credentials or revoke other tokens.
[Authorize(Roles = Roles.ServerAdmin, AuthenticationSchemes = PluginBuilderAuthenticationSchemes.BasicAuth)]
public class AdminAccessTokensController(AdminAccessTokenService tokens) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok(await tokens.List(User.FindFirstValue(ClaimTypes.NameIdentifier)!, cancellationToken));

    [HttpPost]
    public async Task<IActionResult> Create(CreateRequest request, CancellationToken cancellationToken) =>
        StatusCode(201, await tokens.Create(User.FindFirstValue(ClaimTypes.NameIdentifier)!, request.Name, request.ExpiresInDays, cancellationToken));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken cancellationToken) =>
        await tokens.Revoke(User.FindFirstValue(ClaimTypes.NameIdentifier)!, id, cancellationToken) ? NoContent() : NotFound();

    public sealed class CreateRequest
    {
        [Required, MaxLength(100)] public string Name { get; set; } = "";
        [Range(1, 365)] public int ExpiresInDays { get; set; } = 90;
    }
}
