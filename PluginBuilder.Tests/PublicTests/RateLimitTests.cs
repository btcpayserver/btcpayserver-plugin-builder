using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.DataModels;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.PublicTests;

public class RateLimitTests : UnitTestBase
{
    public RateLimitTests(ITestOutputHelper logs) : base(logs)
    {
    }

    [Fact]
    public async Task PublicPluginsEndpoint_AllowsRequestsWithinLimit()
    {
        await using var tester = Create();
        tester.ReuseDatabase = false;
        await tester.Start();

        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.SettingsSetAsync(SettingsKeys.RateLimitPermitLimit, "100");
        await conn.SettingsSetAsync(SettingsKeys.RateLimitWindowSeconds, "60");
        var cache = tester.GetService<AdminSettingsCache>();
        await cache.RefreshAllAdminSettings(conn);

        var client = tester.CreateHttpClient();

        for (var i = 0; i < 5; i++)
        {
            var response = await client.GetAsync("/public/plugins");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task PublicPluginsEndpoint_Returns429WhenRateLimitExceeded()
    {
        await using var tester = Create();
        tester.ReuseDatabase = false;
        await tester.Start();

        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.SettingsSetAsync(SettingsKeys.RateLimitPermitLimit, "2");
        await conn.SettingsSetAsync(SettingsKeys.RateLimitWindowSeconds, "60");
        var cache = tester.GetService<AdminSettingsCache>();
        await cache.RefreshAllAdminSettings(conn);

        var client = tester.CreateHttpClient();

        var r1 = await client.GetAsync("/public/plugins");
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);

        var r2 = await client.GetAsync("/public/plugins");
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);

        var r3 = await client.GetAsync("/public/plugins");
        Assert.Equal(HttpStatusCode.TooManyRequests, r3.StatusCode);
    }

    [Fact]
    public async Task ApiPluginsEndpoint_Returns429WhenRateLimitExceeded()
    {
        await using var tester = Create();
        tester.ReuseDatabase = false;
        await tester.Start();

        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.SettingsSetAsync(SettingsKeys.RateLimitPermitLimit, "2");
        await conn.SettingsSetAsync(SettingsKeys.RateLimitWindowSeconds, "60");
        var cache = tester.GetService<AdminSettingsCache>();
        await cache.RefreshAllAdminSettings(conn);

        var client = tester.CreateHttpClient();

        var r1 = await client.GetAsync("/api/v1/plugins");
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);

        var r2 = await client.GetAsync("/api/v1/plugins");
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);

        var r3 = await client.GetAsync("/api/v1/plugins");
        Assert.Equal(HttpStatusCode.TooManyRequests, r3.StatusCode);
    }

    [Fact]
    public async Task GetPluginDetails_Returns429WhenRateLimitExceeded()
    {
        await using var tester = Create();
        tester.ReuseDatabase = false;
        await tester.Start();

        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.SettingsSetAsync(SettingsKeys.RateLimitPermitLimit, "2");
        await conn.SettingsSetAsync(SettingsKeys.RateLimitWindowSeconds, "60");
        var cache = tester.GetService<AdminSettingsCache>();
        await cache.RefreshAllAdminSettings(conn);

        var client = tester.CreateHttpClient();

        var r1 = await client.GetAsync("/public/plugins/any-slug");
        Assert.NotEqual(HttpStatusCode.TooManyRequests, r1.StatusCode);

        var r2 = await client.GetAsync("/public/plugins/any-slug");
        Assert.NotEqual(HttpStatusCode.TooManyRequests, r2.StatusCode);

        var r3 = await client.GetAsync("/public/plugins/any-slug");
        Assert.Equal(HttpStatusCode.TooManyRequests, r3.StatusCode);
    }

    [Fact]
    public async Task GetPluginVersion_Returns429WhenRateLimitExceeded()
    {
        await using var tester = Create();
        tester.ReuseDatabase = false;
        await tester.Start();

        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.SettingsSetAsync(SettingsKeys.RateLimitPermitLimit, "2");
        await conn.SettingsSetAsync(SettingsKeys.RateLimitWindowSeconds, "60");
        var cache = tester.GetService<AdminSettingsCache>();
        await cache.RefreshAllAdminSettings(conn);

        var client = tester.CreateHttpClient();

        var r1 = await client.GetAsync("/api/v1/plugins/test-slug/versions/1.0.0");
        Assert.NotEqual(HttpStatusCode.TooManyRequests, r1.StatusCode);

        var r2 = await client.GetAsync("/api/v1/plugins/test-slug/versions/1.0.0");
        Assert.NotEqual(HttpStatusCode.TooManyRequests, r2.StatusCode);

        var r3 = await client.GetAsync("/api/v1/plugins/test-slug/versions/1.0.0");
        Assert.Equal(HttpStatusCode.TooManyRequests, r3.StatusCode);
    }

    [Fact]
    public async Task DownloadPlugin_Returns429WhenRateLimitExceeded()
    {
        await using var tester = Create();
        tester.ReuseDatabase = false;
        await tester.Start();

        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.SettingsSetAsync(SettingsKeys.RateLimitPermitLimit, "2");
        await conn.SettingsSetAsync(SettingsKeys.RateLimitWindowSeconds, "60");
        var cache = tester.GetService<AdminSettingsCache>();
        await cache.RefreshAllAdminSettings(conn);

        var client = tester.CreateHttpClient();

        var r1 = await client.GetAsync("/api/v1/plugins/test-slug/versions/1.0.0/download");
        Assert.NotEqual(HttpStatusCode.TooManyRequests, r1.StatusCode);

        var r2 = await client.GetAsync("/api/v1/plugins/test-slug/versions/1.0.0/download");
        Assert.NotEqual(HttpStatusCode.TooManyRequests, r2.StatusCode);

        var r3 = await client.GetAsync("/api/v1/plugins/test-slug/versions/1.0.0/download");
        Assert.Equal(HttpStatusCode.TooManyRequests, r3.StatusCode);
    }

    [Fact]
    public async Task RateLimitWindow_ResetsAfterExpiry()
    {
        await using var tester = Create();
        tester.ReuseDatabase = false;
        await tester.Start();

        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.SettingsSetAsync(SettingsKeys.RateLimitPermitLimit, "2");
        await conn.SettingsSetAsync(SettingsKeys.RateLimitWindowSeconds, "2");
        var cache = tester.GetService<AdminSettingsCache>();
        await cache.RefreshAllAdminSettings(conn);

        var client = tester.CreateHttpClient();

        var r1 = await client.GetAsync("/public/plugins");
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);

        var r2 = await client.GetAsync("/public/plugins");
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);

        var r3 = await client.GetAsync("/public/plugins");
        Assert.Equal(HttpStatusCode.TooManyRequests, r3.StatusCode);

        await Task.Delay(TimeSpan.FromSeconds(3));

        var r4 = await client.GetAsync("/public/plugins");
        Assert.Equal(HttpStatusCode.OK, r4.StatusCode);
    }

    [Fact]
    public async Task SharedRateLimit_AcrossEndpoints()
    {
        await using var tester = Create();
        tester.ReuseDatabase = false;
        await tester.Start();

        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.SettingsSetAsync(SettingsKeys.RateLimitPermitLimit, "2");
        await conn.SettingsSetAsync(SettingsKeys.RateLimitWindowSeconds, "60");
        var cache = tester.GetService<AdminSettingsCache>();
        await cache.RefreshAllAdminSettings(conn);

        var client = tester.CreateHttpClient();

        var r1 = await client.GetAsync("/public/plugins");
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);

        var r2 = await client.GetAsync("/api/v1/plugins");
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);

        var r3 = await client.GetAsync("/public/plugins");
        Assert.Equal(HttpStatusCode.TooManyRequests, r3.StatusCode);
    }

    [Fact]
    public async Task PluginUpdatesEndpoint_Returns429WhenRateLimitExceeded()
    {
        await using var tester = Create();
        tester.ReuseDatabase = false;
        await tester.Start();

        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.SettingsSetAsync(SettingsKeys.RateLimitPermitLimit, "2");
        await conn.SettingsSetAsync(SettingsKeys.RateLimitWindowSeconds, "60");
        var cache = tester.GetService<AdminSettingsCache>();
        await cache.RefreshAllAdminSettings(conn);

        var client = tester.CreateHttpClient();
        var content = new StringContent("[]", Encoding.UTF8, "application/json");

        var r1 = await client.PostAsync("/api/v1/plugins/updates", content);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, r1.StatusCode);

        var r2 = await client.PostAsync("/api/v1/plugins/updates", content);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, r2.StatusCode);

        var r3 = await client.PostAsync("/api/v1/plugins/updates", content);
        Assert.Equal(HttpStatusCode.TooManyRequests, r3.StatusCode);
    }

    [Fact]
    public async Task NonRateLimitedEndpoint_NotAffectedByLimit()
    {
        await using var tester = Create();
        tester.ReuseDatabase = false;
        await tester.Start();

        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.SettingsSetAsync(SettingsKeys.RateLimitPermitLimit, "2");
        await conn.SettingsSetAsync(SettingsKeys.RateLimitWindowSeconds, "60");
        var cache = tester.GetService<AdminSettingsCache>();
        await cache.RefreshAllAdminSettings(conn);

        var client = tester.CreateHttpClient();

        for (var i = 0; i < 5; i++)
        {
            var response = await client.GetAsync("/login");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }
}
