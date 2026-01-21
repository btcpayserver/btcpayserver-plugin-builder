using Dapper;
using PluginBuilder.Services;

namespace PluginBuilder.HostedServices;

/// <summary>
/// Background service that identifies and removes "Zombie" plugins.
/// A Zombie Plugin is defined as a plugin slug that:
/// 1. Has never had a version published (empty versions table).
/// 2. Was created more than 6 months ago (stale).
/// </summary>
public class PluginCleanupHostedService : BackgroundService
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(24);

    private readonly DBConnectionFactory _connectionFactory;
    private readonly ILogger<PluginCleanupHostedService> _logger;

    public PluginCleanupHostedService(
        DBConnectionFactory connectionFactory,
        ILogger<PluginCleanupHostedService> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCleanupAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error during plugin cleanup");
            }

            await Task.Delay(CleanupInterval, stoppingToken);
        }
    }

    /// <summary>
    /// Connects to the database and executes the deletion query.
    /// Safe to run repeatedly; it uses a "NOT EXISTS" check to ensure active plugins are never touched.
    /// </summary>
    private async Task RunCleanupAsync(CancellationToken cancellationToken)
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
    }
}

