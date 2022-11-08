using PluginBuilder.Services;

namespace PluginBuilder.HostedServices
{
    public class AzureStartupHostedService : IHostedService
    {
        public AzureStartupHostedService(AzureStorageClient azureStorageClient)
        {
            AzureStorageClient = azureStorageClient;
        }

        public AzureStorageClient AzureStorageClient { get; }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await AzureStorageClient.EnsureDefaultContainerExists(cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
