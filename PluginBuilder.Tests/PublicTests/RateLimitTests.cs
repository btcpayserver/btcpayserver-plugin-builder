using System.Net;
using System.Text;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.DataModels;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.PublicTests;

public class RateLimitTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    [Theory]
    [InlineData("/public/plugins", "GET")]
    [InlineData("/api/v1/plugins", "GET")]
    [InlineData("/public/plugins/any-slug", "GET")]
    [InlineData("/api/v1/plugins/test-identifier", "GET")]
    [InlineData("/api/v1/plugins/test-slug/versions/1.0.0", "GET")]
    [InlineData("/api/v1/plugins/test-slug/versions/1.0.0/download", "GET")]
    [InlineData("/api/v1/plugins/updates", "POST")]
    [InlineData("/login", "POST")]
    [InlineData("/register", "POST")]
    [InlineData("/forgotpassword", "POST")]
    [InlineData("/passwordreset", "POST")]
    public async Task Endpoint_Returns429WhenRateLimitExceeded(string url, string method)
    {
        var (tester, client) = await SetupRateLimitedTester();
        await using var _ = tester;

        for (var i = 0; i < 2; i++)
        {
            var response = await SendRequest(client, url, method);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }

        var blockedResponse = await SendRequest(client, url, method);
        Assert.Equal(HttpStatusCode.TooManyRequests, blockedResponse.StatusCode);
    }

    [Fact]
    public async Task RateLimitWindow_ResetsAfterExpiry()
    {
        var (tester, client) = await SetupRateLimitedTester(permitLimit: 2, windowSeconds: 2);
        await using var _ = tester;

        for (var i = 0; i < 2; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/public/plugins")).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/public/plugins")).StatusCode);

        await Task.Delay(TimeSpan.FromSeconds(3));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/public/plugins")).StatusCode);
    }

    [Fact]
    public async Task SharedRateLimit_AcrossEndpoints()
    {
        var (tester, client) = await SetupRateLimitedTester();
        await using var _ = tester;

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/public/plugins")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/plugins")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/public/plugins")).StatusCode);
    }

    [Fact]
    public async Task NonRateLimitedEndpoint_NotAffectedByLimit()
    {
        var (tester, client) = await SetupRateLimitedTester();
        await using var _ = tester;

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/login")).StatusCode);
    }

    private async Task<(ServerTester tester, HttpClient client)> SetupRateLimitedTester(int permitLimit = 2, int windowSeconds = 60)
    {
        var tester = Create();
        tester.ReuseDatabase = false;
        await tester.Start();

        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.SettingsSetAsync(SettingsKeys.RateLimitPermitLimit, permitLimit.ToString());
        await conn.SettingsSetAsync(SettingsKeys.RateLimitWindowSeconds, windowSeconds.ToString());
        var cache = tester.GetService<AdminSettingsCache>();
        await cache.RefreshAllAdminSettings(conn);

        var client = tester.CreateHttpClient();
        return (tester, client);
    }

    private static async Task<HttpResponseMessage> SendRequest(HttpClient client, string url, string method)
    {
        return method == "POST"
            ? await client.PostAsync(url, new StringContent("[]", Encoding.UTF8, "application/json"))
            : await client.GetAsync(url);
    }
}
