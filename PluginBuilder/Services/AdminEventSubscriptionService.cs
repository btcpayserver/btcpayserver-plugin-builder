using System.Security.Cryptography;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using MimeKit;

namespace PluginBuilder.Services;

// Shared by the admin API and the admin page so both apply the same validation and secret handling.
public class AdminEventSubscriptionService(DBConnectionFactory connections, IDataProtectionProvider protection)
{
    public const string SecretPurpose = "PluginBuilder.AdminEventWebhooks.v1";

    public record Created(Guid Id, string? Secret);

    public class SubscriptionInfo
    {
        public Guid Id { get; set; }
        public string Kind { get; set; } = "";
        public string Destination { get; set; } = "";
        public string[] EventTypes { get; set; } = [];
        public bool Enabled { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public int FailedDeliveries { get; set; }
    }

    // Returns null when the request is valid, otherwise the message to show.
    public static string? Validate(string kind, string destination, IReadOnlyCollection<string> eventTypes)
    {
        if (eventTypes.Any(t => !AdminEventService.EventTypes.Contains(t)))
            return "Unknown event type. Use an empty array to subscribe to all events.";
        if (kind == "webhook")
            return AdminWebhookSender.IsValidDestination(destination)
                ? null
                : "Webhook destination must be an HTTPS URL without credentials or a fragment.";
        if (kind != "email" || !MailboxAddress.TryParse(destination, out var mailbox) ||
            mailbox.Address != destination || destination.Contains('\r') || destination.Contains('\n'))
            return "Specify kind webhook or email, and a single valid destination.";
        return null;
    }

    // Callers must validate first. The webhook secret is returned here once and stored only encrypted.
    public async Task<Created> Create(string userId, string kind, string destination, IReadOnlyCollection<string> eventTypes,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var secret = kind == "webhook" ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) : null;
        var protectedSecret = secret is null ? null : protection.CreateProtector(SecretPurpose).Protect(secret);
        await using var conn = await connections.Open(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO admin_event_subscriptions(id, kind, destination, protected_secret, event_types, created_by)
            VALUES (@id, @kind, @destination, @protectedSecret, @eventTypes, @userId)
            """, new { id, kind, destination, protectedSecret, eventTypes = eventTypes.Distinct().ToArray(), userId },
            cancellationToken: cancellationToken));
        return new Created(id, secret);
    }

    public async Task<IReadOnlyList<SubscriptionInfo>> List(CancellationToken cancellationToken = default)
    {
        await using var conn = await connections.Open(cancellationToken);
        return (await conn.QueryAsync<SubscriptionInfo>(new CommandDefinition("""
            SELECT s.id, s.kind, s.destination, s.event_types AS EventTypes, s.enabled, s.created_at AS CreatedAt,
                (SELECT count(*) FROM admin_event_deliveries d WHERE d.subscription_id = s.id AND d.status = 'failed')::int AS FailedDeliveries
            FROM admin_event_subscriptions s ORDER BY s.created_at, s.id
            """, cancellationToken: cancellationToken))).ToList();
    }

    public async Task<bool> SetEnabled(Guid id, bool enabled, CancellationToken cancellationToken = default)
    {
        await using var conn = await connections.Open(cancellationToken);
        return await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE admin_event_subscriptions SET enabled = @enabled WHERE id = @id", new { id, enabled },
            cancellationToken: cancellationToken)) > 0;
    }

    public async Task<bool> Delete(Guid id, CancellationToken cancellationToken = default)
    {
        await using var conn = await connections.Open(cancellationToken);
        return await conn.ExecuteAsync(new CommandDefinition("DELETE FROM admin_event_subscriptions WHERE id = @id", new { id },
            cancellationToken: cancellationToken)) > 0;
    }
}
