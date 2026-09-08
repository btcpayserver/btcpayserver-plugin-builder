using System.Data;
using Dapper;

namespace PluginBuilder.Services;

/// <summary>
/// Deletes stale unconfirmed users that have no linked data and are not in the build whitelist.
/// </summary>
public class UserCleanupRunner
{
    private static readonly TimeSpan _staleThreshold = TimeSpan.FromDays(30);

    private readonly DBConnectionFactory _connectionFactory;
    private readonly ILogger<UserCleanupRunner> _logger;

    public UserCleanupRunner(
        DBConnectionFactory connectionFactory,
        ILogger<UserCleanupRunner> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting stale unconfirmed user cleanup...");

        await using var conn = await _connectionFactory.Open(cancellationToken);
        await using var transaction = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition(
            "LOCK TABLE build_whitelist IN SHARE ROW EXCLUSIVE MODE", transaction: transaction,
            cancellationToken: cancellationToken));

        var threshold = DateTimeOffset.UtcNow - _staleThreshold;
        var command = new CommandDefinition(
            """
            DELETE FROM "AspNetUsers" u
            WHERE u."EmailConfirmed" = FALSE
                AND u."CreatedAt" < @Threshold
                AND NOT EXISTS (SELECT 1 FROM build_whitelist w WHERE w.user_id = u."Id")
                AND NOT EXISTS (SELECT 1 FROM "AspNetUserRoles" ur WHERE ur."UserId" = u."Id")
                AND NOT EXISTS (SELECT 1 FROM users_plugins up WHERE up.user_id = u."Id")
                AND NOT EXISTS (SELECT 1 FROM plugin_reviewers pr WHERE pr.user_id = u."Id")
                AND NOT EXISTS (SELECT 1 FROM plugin_reviews pr WHERE pr.helpful_voters ? u."Id")
                AND NOT EXISTS (SELECT 1 FROM plugin_listing_requests plr WHERE plr.reviewed_by = u."Id")
            """,
            new { Threshold = threshold }, transaction,
            cancellationToken: cancellationToken);

        var deletedCount = await conn.ExecuteAsync(command);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation("Deleted {DeletedCount} stale unconfirmed users.", deletedCount);

        return deletedCount;
    }
}
