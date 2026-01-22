using Microsoft.Extensions.DependencyInjection;
using PluginBuilder.Services;

namespace PluginBuilder.HostedServices;

/// <summary>
/// Background service that schedules periodic cleanup of "Zombie" plugins.
/// Delegates actual cleanup logic to <see cref="PluginCleanupRunner"/>.
/// </summary>
public class PluginCleanupHostedService : BackgroundService
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromDays(7);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PluginCleanupHostedService> _logger;

    public PluginCleanupHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<PluginCleanupHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var runner = scope.ServiceProvider.GetRequiredService<PluginCleanupRunner>();
                await runner.RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error during plugin cleanup");
            }

            await Task.Delay(CleanupInterval, stoppingToken);
        }
    }
}
