using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using PluginBuilder.Util.Extensions;

using PluginBuilder.Builds.BuildBroker;

namespace PluginBuilder.Services;

public class AzureStorageClientException(string message) : Exception(message);

/// <summary>
/// Uses the Azure SDK. Artifact uploads read only the file that RemoteBuildSandbox
/// downloaded into its own private directory and verified against its SHA-256; no
/// container can reach that directory.
/// </summary>
public class AzureStorageClient
{
    private static readonly TimeSpan ArtifactUploadTimeout = TimeSpan.FromMinutes(15);
    private readonly CloudBlobClient blobClient;

    public AzureStorageClient(IConfiguration configuration)
    {
        var connectionString = configuration.GetRequired("STORAGE_CONNECTION_STRING");
        if (!CloudStorageAccount.TryParse(connectionString, out var account))
            throw new ConfigurationException("STORAGE_CONNECTION_STRING", "Invalid storage connection string");
        blobClient = account.CreateCloudBlobClient();
    }

    public string DefaultContainer => "artifacts";

    public async Task<bool> EnsureDefaultContainerExists(CancellationToken cancellationToken = default)
    {
        var container = blobClient.GetContainerReference(DefaultContainer);
        return await container.CreateIfNotExistsAsync(
            BlobContainerPublicAccessType.Blob,
            options: null,
            operationContext: null,
            cancellationToken);
    }

    public async Task<bool> IsDefaultContainerAccessible(CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            var container = blobClient.GetContainerReference(DefaultContainer);
            return await container.ExistsAsync(null, null, cts.Token);
        }
        catch
        {
            return false;
        }
    }

    public virtual async Task<string> UploadImageFile(IFormFile file, string blobName)
    {
        var blob = blobClient.GetContainerReference(DefaultContainer).GetBlockBlobReference(blobName);
        blob.Properties.ContentType = file.ContentType;
        using var stream = file.OpenReadStream();
        await blob.UploadFromStreamAsync(stream, null, null, null, CancellationToken.None);
        return blob.Uri.ToString();
    }

    public virtual async Task DeleteImageFileIfExists(string blobName)
    {
        var blob = blobClient.GetContainerReference(DefaultContainer).GetBlockBlobReference(blobName);
        await blob.DeleteIfExistsAsync(DeleteSnapshotsOption.None, null, null, null, CancellationToken.None);
    }

    public async Task<string> UploadStagedArtifact(
        string stagingDirectory,
        string blobName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ArtifactUploadTimeout);
        await using var artifact = OpenStagedArtifact(stagingDirectory);
        var blob = blobClient.GetContainerReference(DefaultContainer).GetBlockBlobReference(blobName);
        blob.Properties.ContentType = "application/zip";
        try
        {
            // Keep this handle and exact byte count for the entire upload. The SDK
            // never reopens a filename or reads beyond the validated byte count.
            await blob.UploadFromStreamAsync(
                artifact,
                artifact.Length,
                AccessCondition.GenerateIfNotExistsCondition(),
                new BlobRequestOptions { MaximumExecutionTime = ArtifactUploadTimeout },
                operationContext: null,
                timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AzureStorageClientException("Timed out while uploading the staged plugin artifact");
        }
        catch (StorageException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (StorageException) when (timeout.IsCancellationRequested)
        {
            throw new AzureStorageClientException("Timed out while uploading the staged plugin artifact");
        }
        catch (StorageException)
        {
            // Storage responses and request URIs can contain credentials or
            // untrusted response text; do not persist them in public build logs.
            throw new AzureStorageClientException("Impossible to upload the staged plugin artifact");
        }
        return blob.Uri.GetComponents(UriComponents.AbsoluteUri, UriFormat.UriEscaped);
    }

    private static FileStream OpenStagedArtifact(string stagingDirectory)
    {
        var file = new FileInfo(Path.Combine(stagingDirectory, "artifact.btcpay"));
        if (!file.Exists || file.Length <= 0 || file.Length > BuildBrokerProtocol.MaximumArtifactBytes)
            throw new AzureStorageClientException(
                $"The staged plugin artifact must be a nonempty regular file of at most {BuildBrokerProtocol.MaximumArtifactBytes / (1024 * 1024)} MiB");
        return new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }
}
