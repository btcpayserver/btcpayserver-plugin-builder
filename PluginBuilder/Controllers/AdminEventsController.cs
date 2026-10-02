using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PluginBuilder.Authentication;
using PluginBuilder.Services;
using PluginBuilder.Util;

namespace PluginBuilder.Controllers;

[ApiController]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/v1/admin/events")]
[Authorize(Roles = Roles.ServerAdmin, AuthenticationSchemes = PluginBuilderAuthenticationSchemes.AdminApi)]
public class AdminEventsController(DBConnectionFactory connections, AdminEventService events, AdminEventSubscriptionService subscriptions) : ControllerBase
{
    public const string SecretPurpose = AdminEventSubscriptionService.SecretPurpose;

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery, Range(0, long.MaxValue)] long after = 0,
        [FromQuery, Range(1, 100)] int limit = 50, [FromQuery] string? type = null, CancellationToken cancellationToken = default)
    {
        if (type is not null && !AdminEventService.EventTypes.Contains(type))
            return BadRequest(new { message = "Unknown event type." });
        var page = await events.Read(after, limit, type, cancellationToken);
        return Ok(new { events = page.Events, nextCursor = page.NextCursor.ToString(System.Globalization.CultureInfo.InvariantCulture), page.HasMore });
    }

    [HttpGet("types")]
    public IActionResult Types() => Ok(AdminEventService.EventTypes);

    [HttpGet("subscriptions")]
    public async Task<IActionResult> Subscriptions(CancellationToken cancellationToken)
    {
        await using var conn = await connections.Open(cancellationToken);
        return Ok(await conn.QueryAsync(new CommandDefinition("""
            SELECT id, kind, destination, event_types AS "eventTypes", enabled,
                   created_by AS "createdBy", created_at AS "createdAt"
            FROM admin_event_subscriptions ORDER BY created_at, id
            """, cancellationToken: cancellationToken)));
    }

    [HttpPost("subscriptions")]
    public async Task<IActionResult> Create(SubscriptionRequest request, CancellationToken cancellationToken)
    {
        if (AdminEventSubscriptionService.Validate(request.Kind, request.Destination, request.EventTypes) is { } error)
            return BadRequest(new { message = error });
        var created = await subscriptions.Create(User.FindFirstValue(ClaimTypes.NameIdentifier)!, request.Kind, request.Destination,
            request.EventTypes, cancellationToken);
        Response.Headers.CacheControl = "no-store";
        return StatusCode(201, new { id = created.Id, secret = created.Secret });
    }

    [HttpPut("subscriptions/{id:guid}/enabled")]
    public async Task<IActionResult> Enable(Guid id, EnableRequest request, CancellationToken cancellationToken) =>
        await subscriptions.SetEnabled(id, request.Enabled, cancellationToken) ? NoContent() : NotFound();

    [HttpDelete("subscriptions/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        await subscriptions.Delete(id, cancellationToken) ? NoContent() : NotFound();

    [HttpGet("subscriptions/{id:guid}/deliveries")]
    public async Task<IActionResult> Deliveries(Guid id, [FromQuery, Range(0, long.MaxValue)] long after = 0,
        [FromQuery, Range(1, 100)] int limit = 50, CancellationToken cancellationToken = default)
    {
        await using var conn = await connections.Open(cancellationToken);
        return Ok(await conn.QueryAsync(new CommandDefinition("""
            SELECT event_id::text AS "eventId", attempts, status, next_attempt_at AS "nextAttemptAt", last_error AS "lastError"
            FROM admin_event_deliveries WHERE subscription_id = @id AND event_id > @after ORDER BY event_id LIMIT @limit
            """, new { id, after, limit }, cancellationToken: cancellationToken)));
    }

    [HttpPost("subscriptions/{id:guid}/deliveries/{eventId:long}/retry")]
    public async Task<IActionResult> Retry(Guid id, long eventId, CancellationToken cancellationToken)
    {
        await using var conn = await connections.Open(cancellationToken);
        var changed = await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE admin_event_deliveries SET status = 'pending', attempts = 0, next_attempt_at = CURRENT_TIMESTAMP, last_error = NULL
            WHERE subscription_id = @id AND event_id = @eventId AND status = 'failed'
            """, new { id, eventId }, cancellationToken: cancellationToken));
        return changed == 0 ? NotFound() : NoContent();
    }

    public sealed class SubscriptionRequest
    {
        [Required] public string Kind { get; set; } = "webhook";
        [Required, MaxLength(2048)] public string Destination { get; set; } = "";
        [Required, MaxLength(20)] public string[] EventTypes { get; set; } = [];
    }

    public sealed class EnableRequest
    {
        public bool Enabled { get; set; }
    }
}
