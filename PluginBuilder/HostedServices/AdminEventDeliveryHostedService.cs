using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Newtonsoft.Json;
using PluginBuilder.Controllers;
using PluginBuilder.Services;

namespace PluginBuilder.HostedServices;

public class AdminEventDeliveryHostedService(DBConnectionFactory connections, AdminWebhookSender webhooks,
    EmailService emails, IDataProtectionProvider protection, ILogger<AdminEventDeliveryHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await DeliverNext(stoppingToken))
                    continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Admin event delivery worker failed; will retry"); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    public async Task<bool> DeliverNext(CancellationToken cancellationToken)
    {
        await using var conn = await connections.Open(cancellationToken);
        await using var transaction = await conn.BeginTransactionAsync(cancellationToken);
        // Keep the delivery row locked through sending: concurrent workers cannot
        // deliver it simultaneously; process death rolls back and makes it retryable.
        var delivery = await conn.QuerySingleOrDefaultAsync<Delivery>(new CommandDefinition("""
            SELECT d.subscription_id AS SubscriptionId, d.attempts, e.id, e.type, e.created_at AS CreatedAt,
                   e.data::text AS Data, s.kind, s.destination, s.protected_secret AS ProtectedSecret
            FROM admin_event_deliveries d
            JOIN admin_event_subscriptions s ON s.id = d.subscription_id
            JOIN admin_events e ON e.id = d.event_id
            WHERE d.status = 'pending' AND d.next_attempt_at <= CURRENT_TIMESTAMP AND s.enabled
            ORDER BY d.next_attempt_at, d.event_id
            LIMIT 1 FOR UPDATE OF d SKIP LOCKED
            """, transaction: transaction, cancellationToken: cancellationToken));
        if (delivery is null)
            return false;

        string? error = null;
        try
        {
            var body = AdminEventService.ToJson(delivery).ToString(Formatting.None);
            if (delivery.Kind == "webhook")
                await webhooks.Send(delivery.Destination,
                    protection.CreateProtector(AdminEventsController.SecretPurpose).Unprotect(delivery.ProtectedSecret!),
                    delivery.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), body, cancellationToken);
            else
                await emails.SendEmail(delivery.Destination, $"Plugin Builder: {delivery.Type}", body, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Destination URLs and response bodies may contain secrets. Store only
            // a safe error category, never arbitrary exception/remote response text.
            error = ex is HttpRequestException { StatusCode: { } status } ? $"HTTP {(int)status}" : ex.GetType().Name;
            logger.LogWarning("Admin event {EventId} delivery to subscription {SubscriptionId} failed: {Error}",
                delivery.Id, delivery.SubscriptionId, error);
        }
        var attempts = delivery.Attempts + 1;
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE admin_event_deliveries SET attempts = @attempts, status = @status,
                last_error = @error, next_attempt_at = CURRENT_TIMESTAMP + @delay
            WHERE subscription_id = @SubscriptionId AND event_id = @Id
            """, new { delivery.SubscriptionId, delivery.Id, attempts, error,
                status = error is null ? "delivered" : attempts >= 10 ? "failed" : "pending",
                delay = TimeSpan.FromSeconds(Math.Min(3600, 30 * Math.Pow(2, attempts - 1))) },
            transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private sealed class Delivery : AdminEventService.EventRow
    {
        public Guid SubscriptionId { get; set; }
        public int Attempts { get; set; }
        public string Kind { get; set; } = "";
        public string Destination { get; set; } = "";
        public string? ProtectedSecret { get; set; }
    }
}
