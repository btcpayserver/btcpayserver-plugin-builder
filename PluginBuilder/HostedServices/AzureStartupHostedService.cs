using PluginBuilder.Services;

namespace PluginBuilder.HostedServices;

public class AzureStartupHostedService : IHostedService
{
    public static bool AzureStartupCompleted { get; private set; }
    public static Exception? AzureStartupError { get; private set; }

    public AzureStartupHostedService(AzureStorageClient azureStorageClient)
    {
        AzureStorageClient = azureStorageClient;
    }

    public AzureStorageClient AzureStorageClient { get; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        AzureStartupCompleted = false;
        AzureStartupError = null;

        try
        {
            await AzureStorageClient.EnsureDefaultContainerExists(cancellationToken);
            AzureStartupCompleted = true;
        }
        catch (Exception ex)
        {
            AzureStartupError = ex;
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
