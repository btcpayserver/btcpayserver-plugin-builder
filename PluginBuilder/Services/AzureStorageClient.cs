using Microsoft.WindowsAzure.Storage;
using Newtonsoft.Json.Linq;
using PluginBuilder.Util.Extensions;

namespace PluginBuilder.Services;

public class AzureStorageClientException : Exception
{
    public AzureStorageClientException(string message) : base(message)
    {
    }
}

/// <summary>
///     A wrapper around "az" utility inside a docker image
///     While we could theorically use the Azure Storage library directly instead of this,
///     the files to upload on azure are stored in a docker volume, so using the library
///     would require us to copy the files to upload out of the docker volume.
///     This wouldn't be ideal, as we would need to make sure to properly clean it up.
///     And we also don't have any datadir for this project.
///     Another solution I tried was to directly use fetch the files via MountPoint of the docker volume
///     Sadly, on windows docker run on a VM, so the file system isn't local to the machine.
/// </summary>
public class AzureStorageClient
{
    private readonly bool isLocalhost;
    private readonly string scheme;

    public AzureStorageClient(ProcessRunner processRunner, IConfiguration configuration)
    {
        ProcessRunner = processRunner;
        StorageConnectionString = configuration.GetRequired("STORAGE_CONNECTION_STRING");
        if (!CloudStorageAccount.TryParse(StorageConnectionString, out var acc))
            throw new ConfigurationException("STORAGE_CONNECTION_STRING", "Invalid storage connection string");
        scheme = acc.BlobEndpoint.Scheme;
        isLocalhost = acc.BlobEndpoint.Host == "localhost" || acc.BlobEndpoint.Host == "127.0.0.1";
        DefaultContainer = "artifacts";
    }

    public ProcessRunner ProcessRunner { get; }
    public string StorageConnectionString { get; }
    public string DefaultContainer { get; }

    public async Task<bool> EnsureDefaultContainerExists(CancellationToken cancellationToken = default)
    {
        OutputCapture error = new();
        OutputCapture output = new();
        var code = await ProcessRunner.RunAsync(
            new ProcessSpec
            {
                Executable = "docker",
                Arguments = CreateArguments("az", "storage", "container", "create", "--name", DefaultContainer, "--public-access", "blob"),
                ErrorCapture = error,
                OutputCapture = output
            }, cancellationToken);
        if (code != 0)
            throw new AzureStorageClientException($"Impossible to create container ({error})");
        return ToJson(output)["created"]!.Value<bool>();
    }

    public async Task<string> Upload(string volume, string fileInVolume, string blobName)
    {
        OutputCapture error = new();
        OutputCapture output = new();
        var code = await ProcessRunner.RunAsync(new ProcessSpec
        {
            Executable = "docker",
            Arguments = CreateArguments(
                new[] { "-v", $"{volume}:/out" },
                new[]
                {
                    "az", "storage", "blob", "upload", "-f", $"/out/{fileInVolume}", "-c", DefaultContainer, "-n", blobName, "--content-type",
                    "application/zip"
                }),
            ErrorCapture = error,
            OutputCapture = output
        }, default);
        if (code != 0)
            throw new AzureStorageClientException($"Impossible to upload ({error})");

        error = new OutputCapture();
        output = new OutputCapture();
        code = await ProcessRunner.RunAsync(
            new ProcessSpec
            {
                Executable = "docker",
                Arguments = CreateArguments("az", "storage", "blob", "url", "--container-name", DefaultContainer, "--name", blobName, "--protocol", scheme),
                ErrorCapture = error,
                OutputCapture = output
            }, default);
        if (code != 0)
            throw new AzureStorageClientException($"Impossible to get the public url of the blob ({error})");
        return ToString(output);
    }

    private static JObject ToJson(OutputCapture output)
    {
        var txt = output.ToString();
        // Remove some crap at the end present for god knows why
        txt = txt.Substring(0, txt.LastIndexOf('}') + 1);
        return JObject.Parse(txt)!;
    }

    private static string ToString(OutputCapture output)
    {
        var txt = output.ToString();
        // Remove some crap at the end present for god knows why
        txt = txt.Substring(0, txt.LastIndexOf('"') + 1);
        return JValue.Parse(txt)!.Value<string>()!;
    }

    private string[] CreateArguments(params string[] args)
    {
        return CreateArguments(null, args);
    }

    private string[] CreateArguments(string[]? dockerArgs, string[] args)
    {
        List<string> a = new();
        a.AddRange(new[] { "run", "--rm", "--env", $"AZURE_STORAGE_CONNECTION_STRING={StorageConnectionString}" });
        if (isLocalhost)
            // Not needed in prod, but we need it in tests to connect to the azure containers running in docker-compose
            a.AddRange(new[] { "--network", "host" });
        if (dockerArgs is not null)
            a.AddRange(dockerArgs);
        a.Add("mcr.microsoft.com/azure-cli:2.9.1");
        a.AddRange(args);
        return a.ToArray();
    }
}
