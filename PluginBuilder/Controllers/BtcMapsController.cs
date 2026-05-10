using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PluginBuilder.APIModels;
using PluginBuilder.Services;

namespace PluginBuilder.Controllers;

[ApiController]
[AllowAnonymous]
[Route("~/apis/btcmaps/v1")]
public sealed class BtcMapsController(
    BtcMapsService btcMapsService,
    ILogger<BtcMapsController> logger)
    : ControllerBase
{
    [HttpGet("ping")]
    public IActionResult Ping() => Ok(new { ok = true, service = "btcmaps", version = "v1" });

    [HttpPost("submit")]
    [EnableRateLimiting(Policies.BtcMapsSubmitRateLimit)]
    public async Task<IActionResult> Submit(
        [FromBody] BtcMapsSubmitRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
            return BadRequest(new { errors = new[] { new ValidationError("body", "Request body is required.") } });

        var errors = btcMapsService.Validate(request);
        if (errors.Count > 0)
            return BadRequest(new { errors });

        var correlationId = Guid.NewGuid().ToString("N");
        BtcMapsDirectoryResult directory;

        try
        {
            directory = await btcMapsService.SubmitToDirectoryAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "BTCMaps directory submission failed (correlationId={CorrelationId}) for {Name} ({Url})",
                correlationId, request.Name, request.Url);
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                error = "directory-upstream-failed",
                correlationId
            });
        }

        return Ok(new BtcMapsSubmitResponse { Directory = directory });
    }
}
