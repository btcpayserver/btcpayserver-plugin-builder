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

        // At least one downstream lane must run; "submit nothing" is almost
        // certainly a caller bug (forgot to set the flag) rather than a legit
        // intent, and silently 200-ing an empty response would hide it.
        if (!request.SubmitToDirectory && !request.SubmitToBtcMap)
        {
            return BadRequest(new { errors = new[] {
                new ValidationError("body", "At least one of SubmitToDirectory or SubmitToBtcMap must be true.")
            }});
        }

        var correlationId = Guid.NewGuid().ToString("N");
        BtcMapsDirectoryResult? directory = null;
        BtcMapsBtcMapResult? btcMap = null;

        if (request.SubmitToDirectory)
        {
            try
            {
                directory = await btcMapsService.SubmitToDirectoryAsync(request, cancellationToken);
            }
            catch (BtcMapsService.DirectoryTokenMissingException ex)
            {
                logger.LogError(ex, "BTCMaps directory submission rejected: token not configured (correlationId={CorrelationId})", correlationId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "directory-not-configured", correlationId });
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation(ex, "BTCMaps directory submission cancelled by caller (correlationId={CorrelationId})", correlationId);
                throw;
            }
            catch (OperationCanceledException ex)
            {
                logger.LogError(ex, "BTCMaps directory submission timed out upstream (correlationId={CorrelationId}) for {Name} ({Url})",
                    correlationId, request.Name, request.Url);
                return StatusCode(StatusCodes.Status504GatewayTimeout, new { error = "directory-upstream-timeout", correlationId });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "BTCMaps directory submission failed (correlationId={CorrelationId}) for {Name} ({Url})",
                    correlationId, request.Name, request.Url);
                return StatusCode(StatusCodes.Status502BadGateway, new { error = "directory-upstream-failed", correlationId });
            }
        }

        if (request.SubmitToBtcMap)
        {
            try
            {
                btcMap = await btcMapsService.SubmitToBtcMapAsync(request, cancellationToken);
            }
            catch (BtcMapsService.BtcMapTokenMissingException ex)
            {
                logger.LogError(ex, "BTCMaps import submission rejected: token not configured (correlationId={CorrelationId})", correlationId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "btcmap-not-configured", correlationId });
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation(ex, "BTCMaps import submission cancelled by caller (correlationId={CorrelationId})", correlationId);
                throw;
            }
            catch (OperationCanceledException ex)
            {
                logger.LogError(ex, "BTCMaps import submission timed out upstream (correlationId={CorrelationId}) for {Name} ({Url})",
                    correlationId, request.Name, request.Url);
                return StatusCode(StatusCodes.Status504GatewayTimeout, new { error = "btcmap-upstream-timeout", correlationId });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "BTCMaps import submission failed (correlationId={CorrelationId}) for {Name} ({Url})",
                    correlationId, request.Name, request.Url);
                return StatusCode(StatusCodes.Status502BadGateway, new { error = "btcmap-upstream-failed", correlationId });
            }
        }

        return Ok(new BtcMapsSubmitResponse { Directory = directory, BtcMap = btcMap });
    }
}
