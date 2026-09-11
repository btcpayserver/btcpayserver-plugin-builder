using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using MimeKit;
using PluginBuilder.Authentication;
using PluginBuilder.Services;
using PluginBuilder.Util;

namespace PluginBuilder.Controllers;

[ApiController]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/v1/admin/events")]
[Authorize(Roles = Roles.ServerAdmin, AuthenticationSchemes = PluginBuilderAuthenticationSchemes.AdminApi)]
public class AdminEventsController(DBConnectionFactory connections, AdminEventService events, IDataProtectionProvider protection) : ControllerBase
{
    public const string SecretPurpose = "PluginBuilder.AdminEventWebhooks.v1";

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
        if (request.EventTypes.Any(t => !AdminEventService.EventTypes.Contains(t)))
            return BadRequest(new { message = "Unknown event type. Use an empty array to subscribe to all events." });
        if (request.Kind == "webhook")
        {
            if (!AdminWebhookSender.IsValidDestination(request.Destination))
                return BadRequest(new { message = "Webhook destination must be an HTTPS URL without credentials or a fragment." });
        }
        else if (request.Kind != "email" || !MailboxAddress.TryParse(request.Destination, out var mailbox) ||
                 mailbox.Address != request.Destination || request.Destination.Contains('\r') || request.Destination.Contains('\n'))
            return BadRequest(new { message = "Specify kind webhook or email, and a single valid destination." });

        var id = Guid.NewGuid();
        var secret = request.Kind == "webhook" ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) : null;
        var protectedSecret = secret is null ? null : protection.CreateProtector(SecretPurpose).Protect(secret);
        await using var conn = await connections.Open(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO admin_event_subscriptions(id, kind, destination, protected_secret, event_types, created_by)
            VALUES (@id, @Kind, @Destination, @protectedSecret, @eventTypes, @createdBy)
            """, new { id, request.Kind, request.Destination, protectedSecret,
                eventTypes = request.EventTypes.Distinct().ToArray(), createdBy = User.FindFirstValue(ClaimTypes.NameIdentifier)! },
            cancellationToken: cancellationToken));
        Response.Headers.CacheControl = "no-store";
        return StatusCode(201, new { id, secret });
    }

    [HttpPut("subscriptions/{id:guid}/enabled")]
    public async Task<IActionResult> Enable(Guid id, EnableRequest request, CancellationToken cancellationToken)
    {
        await using var conn = await connections.Open(cancellationToken);
        var changed = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE admin_event_subscriptions SET enabled = @Enabled WHERE id = @id", new { id, request.Enabled },
            cancellationToken: cancellationToken));
        return changed == 0 ? NotFound() : NoContent();
    }

    [HttpDelete("subscriptions/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await using var conn = await connections.Open(cancellationToken);
        var changed = await conn.ExecuteAsync(new CommandDefinition("DELETE FROM admin_event_subscriptions WHERE id = @id", new { id },
            cancellationToken: cancellationToken));
        return changed == 0 ? NotFound() : NoContent();
    }

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
