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
        catch (BtcMapsService.DirectoryTokenMissingException ex)
        {
            // Missing token is a server-side deployment / configuration outage,
            // not a normal "skipped" outcome. Surface 503 so clients (and ops)
            // can distinguish it from an accepted submission.
            logger.LogError(ex, "BTCMaps directory submission rejected: token not configured (correlationId={CorrelationId})", correlationId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "directory-not-configured",
                correlationId
            });
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
