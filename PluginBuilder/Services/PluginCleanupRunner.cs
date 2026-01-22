using Dapper;
using Microsoft.Extensions.Logging;

namespace PluginBuilder.Services;

/// <summary>
/// Executes the cleanup logic for "Zombie" plugins.
/// A Zombie Plugin is defined as a plugin slug that:
/// 1. Has never had a version published (empty versions table).
/// 2. Was created more than 6 months ago (stale).
/// </summary>
public class PluginCleanupRunner
{
    private readonly DBConnectionFactory _connectionFactory;
    private readonly ILogger<PluginCleanupRunner> _logger;

    public PluginCleanupRunner(
        DBConnectionFactory connectionFactory,
        ILogger<PluginCleanupRunner> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    /// <summary>
    /// Connects to the database and deletes stale plugins without versions.
    /// Safe to run repeatedly; it uses a "NOT EXISTS" check to ensure active plugins are never touched.
    /// </summary>
    /// <returns>The number of deleted plugins.</returns>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting cleanup...");

        await using var conn = await _connectionFactory.Open(cancellationToken);

        var threshold = DateTimeOffset.UtcNow.AddMonths(-6);
        var deletedCount = await conn.ExecuteAsync(
            """
            DELETE FROM plugins 
            WHERE added_at < @Threshold 
            AND NOT EXISTS (SELECT 1 FROM versions WHERE plugin_slug = plugins.slug)
            """,
            new { Threshold = threshold });

        _logger.LogInformation("Deleted {DeletedCount} stale plugins.", deletedCount);

        return deletedCount;
    }
}

