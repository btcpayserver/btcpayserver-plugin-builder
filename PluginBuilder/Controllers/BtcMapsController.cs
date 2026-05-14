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
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            // Caller cancelled (client disconnect, request abort). Rethrow so
            // the pipeline drops the connection without producing a response
            // body the client will never read.
            logger.LogInformation(ex, "BTCMaps directory submission cancelled by caller (correlationId={CorrelationId})", correlationId);
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // OCE without caller cancellation = HttpClient.Timeout surfacing as
            // TaskCanceledException. Treat as an upstream timeout, distinct
            // from a generic 502 so ops + the plugin client can tell them apart.
            logger.LogError(ex, "BTCMaps directory submission timed out upstream (correlationId={CorrelationId}) for {Name} ({Url})",
                correlationId, request.Name, request.Url);
            return StatusCode(StatusCodes.Status504GatewayTimeout, new
            {
                error = "directory-upstream-timeout",
                correlationId
            });
        }
        catch (Exception ex)
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
