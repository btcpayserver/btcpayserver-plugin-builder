using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using PluginBuilder.APIModels;

namespace PluginBuilder.Tests
{
    public static class HttpClientExtensions
    {
        static JsonSerializerSettings serializerSettings = new JsonSerializerSettings() { ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver() };
        public static async Task<PublishedVersion[]> GetPublishedVersions(this HttpClient httpClient, string btcpayVersion, bool includePreRelease)
        {
            var result = await httpClient.GetStringAsync($"api/v1/plugins?btcpayVersion={btcpayVersion}&includePreRelease={includePreRelease}");
            return JsonConvert.DeserializeObject<PublishedVersion[]>(result, serializerSettings) ?? throw new InvalidOperationException();
        }
        public static async Task<byte[]> DownloadPlugin(this HttpClient httpClient, PluginSelector pluginSelector, PluginVersion pluginVersion)
        {
            return await httpClient.GetByteArrayAsync($"api/v1/plugins/{pluginSelector}/versions/{pluginVersion}/download");
        }
    }
}
