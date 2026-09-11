using Dapper;
using PluginBuilder.Util.Extensions;

namespace PluginBuilder.Services;

public class AdminHistoryRetention
{
    private readonly DBConnectionFactory _connections;
    public int Days { get; }

    public AdminHistoryRetention(DBConnectionFactory connections, IConfiguration configuration)
    {
        _connections = connections;
        Days = configuration.GetValue("ADMIN_HISTORY_RETENTION_DAYS", 365);
        if (Days is < 1 or > 3650)
            throw new ConfigurationException("ADMIN_HISTORY_RETENTION_DAYS", "Must be between 1 and 3650 days.");
    }

    public async Task<int> Purge(CancellationToken cancellationToken)
    {
        await using var conn = await _connections.Open(cancellationToken);
        await using var transaction = await conn.BeginTransactionAsync(cancellationToken);
        // One cutoff and transaction for event payloads, dependent deliveries,
        // audit identities and retired token metadata. Never reset stream IDs.
        var cutoff = DateTimeOffset.UtcNow.AddDays(-Days);
        var deleted = await conn.ExecuteAsync(new CommandDefinition("""
            DELETE FROM admin_events WHERE id IN (
                SELECT id FROM admin_events WHERE created_at < @cutoff ORDER BY created_at LIMIT 1000 FOR UPDATE SKIP LOCKED);
            DELETE FROM admin_api_audit WHERE id IN (
                SELECT id FROM admin_api_audit WHERE started_at < @cutoff ORDER BY started_at LIMIT 1000 FOR UPDATE SKIP LOCKED);
            DELETE FROM admin_access_tokens WHERE id IN (
                SELECT id FROM admin_access_tokens WHERE COALESCE(revoked_at, expires_at) < @cutoff LIMIT 1000 FOR UPDATE SKIP LOCKED);
            DELETE FROM admin_event_first_builds WHERE user_id IN (
                SELECT user_id FROM admin_event_first_builds f WHERE NOT EXISTS (SELECT 1 FROM "AspNetUsers" u WHERE u."Id" = f.user_id) LIMIT 1000);
            """, new { cutoff }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }
}
