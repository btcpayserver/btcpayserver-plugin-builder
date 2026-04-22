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
    [EnableRateLimiting(Policies.PublicApiRateLimit)]
    public async Task<IActionResult> Submit(
        [FromBody] BtcMapsSubmitRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
            return BadRequest(new { errors = new[] { new ValidationError("body", "Request body is required.") } });

        if (!request.SubmitToDirectory && !request.TagOnOsm)
            return BadRequest(new { errors = new[] { new ValidationError("action", "Set submitToDirectory and/or tagOnOsm to true.") } });

        var errors = btcMapsService.Validate(request);
        if (errors.Count > 0)
            return BadRequest(new { errors });

        var response = new BtcMapsSubmitResponse();

        if (request.SubmitToDirectory)
        {
            try
            {
                response.Directory = await btcMapsService.SubmitToDirectoryAsync(request, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "BTCMaps directory submission failed for {Name} ({Url})", request.Name, request.Url);
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    error = "directory-upstream-failed",
                    detail = ex.Message
                });
            }
        }

        if (request.TagOnOsm)
        {
            try
            {
                response.Osm = await btcMapsService.TagOnOsmAsync(request, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "BTCMaps OSM tagging failed for {Name} node {NodeType}/{NodeId}",
                    request.Name, request.OsmNodeType, request.OsmNodeId);
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    error = "osm-upstream-failed",
                    detail = ex.Message,
                    partial = response
                });
            }
        }

        return Ok(response);
    }
}
