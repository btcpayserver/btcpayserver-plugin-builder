using System.Net.Http.Headers;
using System.Text;

namespace PluginBuilder.Tests;

public static class BasicAuthHttpClientExtensions
{
    public static HttpClient SetBasicAuth(this HttpClient httpClient, string username, string password)
    {
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        return httpClient;
    }
}
