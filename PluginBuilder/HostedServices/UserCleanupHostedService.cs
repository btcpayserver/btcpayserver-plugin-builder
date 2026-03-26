using PluginBuilder.Services;

namespace PluginBuilder.HostedServices;

public class UserCleanupHostedService : BackgroundService
{
    private static readonly TimeSpan _cleanupInterval = TimeSpan.FromDays(7);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UserCleanupHostedService> _logger;

    public UserCleanupHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<UserCleanupHostedService> logger)
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
                var runner = scope.ServiceProvider.GetRequiredService<UserCleanupRunner>();
                await runner.RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error during user cleanup");
            }

            await Task.Delay(_cleanupInterval, stoppingToken);
        }
    }
}
