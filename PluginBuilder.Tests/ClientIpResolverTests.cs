using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using Xunit;

namespace PluginBuilder.Tests;

public class ClientIpResolverTests
{
    [Theory]
    [InlineData(null, "198.51.100.7", "203.0.113.99", "198.51.100.7")]
    [InlineData(null, null, "203.0.113.99", null)]
    [InlineData(null, "2001:db8::7", null, "2001:db8::7")]
    [InlineData(null, "::ffff:198.51.100.7", null, "198.51.100.7")]
    [InlineData(null, "fe80::1%2", null, "fe80::1")]
    [InlineData(null, "173.245.48.1", null, "173.245.48.1")]
    [InlineData("192.0.2.10", "192.0.2.10", "203.0.113.99, 198.51.100.7", "198.51.100.7")]
    [InlineData("192.0.2.0/24", "192.0.2.10", "173.245.48.1", "173.245.48.1")]
    [InlineData("192.0.2.10", "192.0.2.11", "198.51.100.7", "192.0.2.11")]
    [InlineData("192.0.2.10", "192.0.2.10", "203.0.113.99, invalid", null)]
    [InlineData("192.0.2.10", "192.0.2.10", "203.0.113.99, ", null)]
    [InlineData("192.0.2.10", "192.0.2.10", null, null)]
    [InlineData("192.0.2.10", "192.0.2.10", "fe80::1%2", null)]
    [InlineData("2001:db8::/32", "2001:db8::10", "2001:db8:1::7", "2001:db8:1::7")]
    [InlineData("192.0.2.10", "::ffff:192.0.2.10", "::ffff:198.51.100.7", "198.51.100.7")]
    [InlineData("::ffff:192.0.2.0/120", "192.0.2.10", "198.51.100.7", "198.51.100.7")]
    [InlineData("198.51.100.1; 192.0.2.10\n2001:db8::/32", "192.0.2.10", "198.51.100.7", "198.51.100.7")]
    public void ResolvesOnlyTheSocketOrTrustedNginxPeer(string? proxies, string? socket, string? forwarded, string? expected)
    {
        var http = NewContext(socket);
        http.Request.Headers["X-Forwarded-For"] = forwarded;
        http.Request.Headers["CF-Connecting-IP"] = "203.0.113.9";
        http.Request.Headers["X-Original-For"] = "203.0.113.99";

        Resolver(proxies).Capture(http);

        Assert.Equal(expected, ClientIpResolver.Get(http)?.ToString());
    }

    [Theory]
    [InlineData("hostname")]
    [InlineData("192.0.2.0/99")]
    [InlineData("192.0.2.10, invalid")]
    [InlineData("fe80::1%2")]
    public void InvalidProxyConfigurationFailsAtStartup(string proxies) =>
        Assert.Throws<ConfigurationException>(() => Resolver(proxies));

    [Theory]
    [InlineData("198.51.100.7")]
    [InlineData(null)]
    public void CaptureRunsOnlyOnceEvenWithoutAnAddress(string? socket)
    {
        var resolver = Resolver();
        var http = NewContext(socket);
        Assert.Null(ClientIpResolver.Get(http));
        resolver.Capture(http);
        http.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.99");
        http.Request.Headers["X-Forwarded-For"] = "198.51.100.99";

        resolver.Capture(http);

        Assert.Equal(socket, ClientIpResolver.Get(http)?.ToString());
    }

    private static ClientIpResolver Resolver(string? proxies = null) => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["CLIENT_IP_TRUSTED_PROXIES"] = proxies }).Build());

    private static DefaultHttpContext NewContext(string? socket) => new()
    {
        Connection = { RemoteIpAddress = socket is null ? null : IPAddress.Parse(socket) }
    };
}
