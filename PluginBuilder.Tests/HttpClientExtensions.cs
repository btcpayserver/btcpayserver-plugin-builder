using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using PluginBuilder.APIModels;

namespace PluginBuilder.Tests;

public static class HttpClientExtensions
{
    private static readonly JsonSerializerSettings serializerSettings = new() { ContractResolver = new CamelCasePropertyNamesContractResolver() };

    public static async Task<PublishedVersion[]> GetPublishedVersions(this HttpClient httpClient,
        string btcpayVersion,
        bool includePreRelease,
        bool? includeAllVersions = false,
        string? searchPluginName = null
    )
    {
        var url = $"api/v1/plugins?btcpayVersion={btcpayVersion}&includePreRelease={includePreRelease}&includeAllVersions={includeAllVersions}";
        if (!string.IsNullOrEmpty(searchPluginName))
            url += $"&searchPluginName={Uri.EscapeDataString(searchPluginName)}";

        var result = await httpClient.GetStringAsync(url);
        return JsonConvert.DeserializeObject<PublishedVersion[]>(result, serializerSettings) ?? throw new InvalidOperationException();
    }

    public static async Task<PublishedVersion?> GetPlugin(this HttpClient httpClient, string pluginSlug, string version)
    {
        try
        {
            var result = await httpClient.GetStringAsync($"api/v1/plugins/{pluginSlug}/versions/{version}");
            return JsonConvert.DeserializeObject<PublishedVersion?>(result, serializerSettings);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public static async Task<byte[]> DownloadPlugin(this HttpClient httpClient, PluginSelector pluginSelector, PluginVersion pluginVersion)
    {
        return await httpClient.GetByteArrayAsync($"api/v1/plugins/{pluginSelector}/versions/{pluginVersion}/download");
    }
}
