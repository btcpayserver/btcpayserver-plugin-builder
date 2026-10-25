using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Channels;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using PluginBuilder.HostedServices;
using PluginBuilder.Services;
using PluginBuilder.Util;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests;

public class AccessLogTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    private const string Password = "test-password:with-colons:123";

    private static string Datagram(string ip, string path = "/register", int status = 200, string ua = "curl/8.5.0",
        string time = "2026-10-22T16:40:00+00:00") =>
        "<190>Oct 22 16:40:00 pbaccess: {\"time\":\"" + time + "\",\"ip\":\"" + ip +
        "\",\"host\":\"plugin-builder.btcpayserver.org\",\"method\":\"POST\",\"path\":\"" + path + "\",\"status\":" + status +
        ",\"bytes\":512,\"ua\":\"" + ua + "\"}";

    private static bool Parse(string datagram, out AccessLogEntry? entry) => AccessLogLine.TryParse(Encoding.UTF8.GetBytes(datagram), out entry);

    [Fact]
    public void ParsesTheNginxSyslogLine()
    {
        Assert.True(Parse(Datagram("203.0.113.7"), out var entry));
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 16, 40, 0, TimeSpan.Zero), entry!.LoggedAt);
        Assert.Equal(IPAddress.Parse("203.0.113.7"), entry.Ip);
        Assert.Equal(("plugin-builder.btcpayserver.org", "POST", "/register", 200, 512L, "curl/8.5.0"),
            (entry.Host, entry.Method, entry.Path, entry.Status, entry.Bytes, entry.UserAgent));

        // nginx's local time with an offset is stored as UTC; a v4-mapped address is stored as IPv4.
        Assert.True(Parse(Datagram("::ffff:198.51.100.1", time: "2026-10-22T11:40:00-05:00"), out entry));
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 16, 40, 0, TimeSpan.Zero), entry!.LoggedAt);
        Assert.Equal(TimeSpan.Zero, entry.LoggedAt.Offset);
        Assert.Equal(IPAddress.Parse("198.51.100.1"), entry.Ip);
    }

    [Fact]
    public void ParsesDatagramsCapturedFromNginx()
    {
        // Sent by nginx:1.25.3-bookworm with the infra repository's pbaccess format, for GET /a%22b/%C3%A9t%C3%A9 with a
        // quoted user agent, and for a request nginx could not parse at all (400).
        Assert.True(Parse("""<190>Oct 22 16:46:32 pbaccess: {"time":"2026-10-22T16:46:32+00:00","ip":"172.17.0.1","host":"127.0.0.1","method":"GET","path":"/a\"b/été","status":200,"bytes":3,"ua":"Mozilla/5.0 \"quoted\" \\ back"}""",
            out var entry));
        Assert.Equal(("/a\"b/été", "Mozilla/5.0 \"quoted\" \\ back"), (entry!.Path, entry.UserAgent));
        Assert.True(Parse("""<190>Oct 22 16:46:41 pbaccess: {"time":"2026-10-22T16:46:41+00:00","ip":"172.17.0.1","host":"","method":"","path":"","status":400,"bytes":157,"ua":""}""",
            out entry));
        Assert.Equal((400, "", ""), (entry!.Status, entry.Method, entry.Path));
    }

    [Fact]
    public void LogsARequestWhoseUserAgentIsNotUtf8()
    {
        // As nginx sent it for a user agent of bad, 0xFF 0xFE, ua, an emoji and end: raw bytes, not valid UTF-8.
        var datagram = Encoding.ASCII.GetBytes("<190>Oct 22 16:48:13 pbaccess: {\"time\":\"2026-10-22T16:48:13+00:00\",\"ip\":\"172.17.0.1\"," +
                "\"host\":\"a\",\"method\":\"GET\",\"path\":\"/x\",\"status\":200,\"bytes\":3,\"ua\":\"bad")
            .Concat(new byte[] { 0xff, 0xfe }).Concat(Encoding.UTF8.GetBytes("ua\U0001F600end\"}")).ToArray();
        Assert.True(AccessLogLine.TryParse(datagram, out var entry));
        Assert.Equal("bad\uFFFD\uFFFDua\U0001F600end", entry!.UserAgent);
    }

    [Fact]
    public void AcceptsTheSpacePaddedDayNginxWrites() =>
        Assert.True(Parse("<190>Oct  2 16:40:00 pbaccess: {\"time\":\"2026-10-02T16:40:00+00:00\",\"ip\":\"203.0.113.7\",\"status\":200,\"bytes\":0}", out _));

    [Fact]
    public void NeverKeepsHalfASurrogatePair()
    {
        Assert.True(Parse(Datagram("203.0.113.7", ua: new string('a', AccessLogLine.MaxUserAgent - 1) + "\U0001F600"), out var entry));
        Assert.Equal(new string('a', AccessLogLine.MaxUserAgent - 1), entry!.UserAgent);
        Assert.Equal(entry.UserAgent, Encoding.UTF8.GetString(new UTF8Encoding(false, true).GetBytes(entry.UserAgent)));
    }

    [Fact]
    public void NeutralizesControlCharactersAndCapsLengths()
    {
        Assert.True(Parse(Datagram("2001:db8::1", path: "/" + new string('a', 5000), ua: "evil\\u0000agent\\n\\u2028x"), out var entry));
        Assert.Equal("evil agent  x", entry!.UserAgent);
        Assert.Equal(AccessLogLine.MaxPath, entry.Path.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("pbaccess: {}")]
    [InlineData("<190>Oct 22 16:40:00 other: {\"time\":\"2026-10-22T16:40:00+00:00\",\"ip\":\"203.0.113.7\",\"status\":200,\"bytes\":0}")]
    [InlineData("<190>Oct 22 16:40:00 pbaccess: not json")]
    [InlineData("<190>Oct 22 16:40:00 pbaccess: [1,2]")]
    [InlineData("<190>Oct 22 16:40:00 pbaccess: {\"time\":\"2026-10-22T16:40:00+00:00\",\"ip\":\"not-an-ip\",\"status\":200,\"bytes\":0}")]
    [InlineData("<190>Oct 22 16:40:00 pbaccess: {\"time\":\"yesterday\",\"ip\":\"203.0.113.7\",\"status\":200,\"bytes\":0}")]
    [InlineData("<190>Oct 22 16:40:00 pbaccess: {\"time\":\"2026-10-22T16:40:00+00:00\",\"ip\":\"203.0.113.7\",\"status\":99,\"bytes\":0}")]
    [InlineData("<190>Oct 22 16:40:00 pbaccess: {\"time\":\"2026-10-22T16:40:00+00:00\",\"ip\":\"203.0.113.7\",\"status\":\"200\",\"bytes\":0}")]
    [InlineData("<190>Oct 22 16:40:00 pbaccess: {\"time\":\"2026-10-22T16:40:00+00:00\",\"ip\":\"203.0.113.7\",\"status\":200,\"bytes\":-1}")]
    [InlineData("<190>Oct 22 16:40:00 pbaccess: {\"time\":\"2026-10-22T16:40:00+00:00\",\"status\":200,\"bytes\":0}")]
    // Another program's line that merely contains the tag: the tag must be the header's own.
    [InlineData("<190>Oct 22 16:40:00 other: pbaccess: {\"time\":\"2026-10-22T16:40:00+00:00\",\"ip\":\"203.0.113.7\",\"status\":200,\"bytes\":0}")]
    // A lone surrogate escape: nginx never writes one, but anything that can reach the port can.
    [InlineData("<190>Oct 22 16:40:00 pbaccess: {\"time\":\"2026-10-22T16:40:00+00:00\",\"ip\":\"203.0.113.7\",\"status\":200,\"bytes\":0,\"ua\":\"\\ud800\"}")]
    public void RefusesAnythingElse(string datagram) => Assert.False(Parse(datagram, out _));

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7/32")]
    [InlineData("203.0.113.0/24", "203.0.113.0/24")]
    [InlineData("2001:db8::1/64", "2001:db8::1/64")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7/32")]
    [InlineData("2001:db8::1", "2001:db8::1/128")]
    [InlineData("::ffff:203.0.113.0/120", "203.0.113.0/24")]
    [InlineData("::ffff:203.0.113.7/128", "203.0.113.7/32")]
    [InlineData("::ffff:203.0.113.7/96", "203.0.113.7/0")]
    [InlineData("::ffff:203.0.113.7/95", "::ffff:203.0.113.7/95")]
    public void ParsesAnAddressOrBlock(string value, string expected)
    {
        Assert.True(AccessLogLine.TryParseNetwork(value, out var network));
        Assert.Equal(expected, network);
    }

    [Theory]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("203.0.113.7/33")]
    [InlineData("203.0.113.7/-1")]
    [InlineData("203.0.113.7/")]
    [InlineData("2001:db8::1/129")]
    [InlineData("::ffff:203.0.113.7/129")]
    [InlineData("fe80::1%2")]
    [InlineData("10/8")]
    [InlineData("172.16/12")]
    [InlineData("192.168/16")]
    [InlineData("192.168.010.001")]
    [InlineData("0x0a.1.2.3")]
    [InlineData("3232238081")]
    public void RefusesAnInvalidAddressOrBlock(string value) => Assert.False(AccessLogLine.TryParseNetwork(value, out _));

    [Fact]
    public async Task CountsDroppedEntriesWhenTheQueueIsFull()
    {
        using var receiver = new AccessLogHostedService(null!, new ConfigurationBuilder().Build(), NullLogger<AccessLogHostedService>.Instance);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var queue = (Channel<AccessLogEntry>)typeof(AccessLogHostedService).GetField("_queue", flags)!.GetValue(receiver)!;
        var dropped = typeof(AccessLogHostedService).GetField("_dropped", flags)!;
        Assert.True(Parse(Datagram("203.0.113.7"), out var entry));
        for (var i = 0; i < 50_000; i++)
            Assert.True(queue.Writer.TryWrite(entry!));

        // Run only the receiver so the writer cannot drain the full queue while this datagram arrives.
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var receive = (Task)typeof(AccessLogHostedService).GetMethod("ReceiveLoop", flags)!
            .Invoke(receiver, [listener, cancellation.Token])!;
        try
        {
            using var sender = new UdpClient();
            var datagram = Encoding.UTF8.GetBytes(Datagram("198.51.100.1"));
            await sender.SendAsync(datagram, (IPEndPoint)listener.Client.LocalEndPoint!);
            for (var i = 0; i < 100 && (long)dropped.GetValue(receiver)! == 0; i++)
                await Task.Delay(20);

            Assert.Equal(1L, (long)dropped.GetValue(receiver)!);
            Assert.Equal(50_000, queue.Reader.Count);
            while (queue.Reader.TryRead(out var retained))
                Assert.Equal(entry, retained);
        }
        finally
        {
            await cancellation.CancelAsync();
            await receive.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task ReceivesSearchesAndPurgesTheAccessLog()
    {
        var tester = Create();
        tester.ReuseDatabase = false;
        tester.ConfigureApplication = app =>
        {
            app.Configuration["ACCESSLOG_SYSLOG_PORT"] = "0";
            app.Configuration["ACCESSLOG_RETENTION_DAYS"] = "7";
        };
        tester.ConfigureServices = services =>
        {
            // Keep the factory registration for AccessLogHostedService so the real host starts and stops it.
            foreach (var service in services.Where(s => s.ServiceType == typeof(IHostedService) &&
                         s.ImplementationType != typeof(DatabaseStartupHostedService) && s.ImplementationFactory is null).ToArray())
                services.Remove(service);
        };
        await using var _ = tester;
        await tester.Start();
        var receiver = tester.GetService<AccessLogHostedService>();
        Assert.Same(receiver, Assert.Single(tester.WebApp.Services.GetServices<IHostedService>().OfType<AccessLogHostedService>()));
        var port = await receiver.Bound.WaitAsync(TimeSpan.FromSeconds(10));
        using (var scope = tester.WebApp.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            foreach (var (email, isAdmin) in new[] { ("admin@access-log.test", true), ("user@access-log.test", false) })
            {
                var account = new IdentityUser { UserName = email, Email = email, EmailConfirmed = true };
                Assert.True((await users.CreateAsync(account, Password)).Succeeded);
                if (isAdmin)
                    Assert.True((await users.AddToRoleAsync(account, Roles.ServerAdmin)).Succeeded);
            }
        }

        using var udp = new UdpClient();
        var now = DateTimeOffset.UtcNow;
        string At(int minutesAgo) => now.AddMinutes(-minutesAgo).ToString("yyyy-MM-ddTHH:mm:sszzz");
        foreach (var datagram in new[]
                 {
                     Datagram("2001:db8:1:2::10", time: At(30)), Datagram("2001:db8:1:2::20", "/plugins/x", time: At(20)),
                     "garbage", Datagram("::ffff:203.0.113.7", "/register", 400, time: At(10))
                 })
        {
            var bytes = Encoding.UTF8.GetBytes(datagram);
            await udp.SendAsync(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, port));
        }

        using var admin = tester.CreateHttpClient().SetBasicAuth("admin@access-log.test", Password);
        JObject page = new();
        for (var i = 0; i < 100; i++)
        {
            page = JObject.Parse(await admin.GetStringAsync("/api/v1/admin/access-log"));
            if (((JArray)page["items"]!).Count == 3)
                break;
            await Task.Delay(100);
        }
        var items = (JArray)page["items"]!;
        Assert.Equal(new[] { "203.0.113.7", "2001:db8:1:2::20", "2001:db8:1:2::10" }, items.Select(i => i.Value<string>("ip")));
        Assert.Equal(400, items[0].Value<int>("status"));
        Assert.Equal("/register", items[0].Value<string>("path"));
        Assert.Equal("curl/8.5.0", items[0].Value<string>("userAgent"));
        Assert.False(page.Value<bool>("hasMore"));

        async Task<string[]> Ips(string query) =>
            ((JArray)JObject.Parse(await admin.GetStringAsync("/api/v1/admin/access-log?" + query))["items"]!)
            .Select(i => i.Value<string>("ip")!).ToArray();
        Assert.Equal(new[] { "203.0.113.7" }, await Ips("ip=203.0.113.7"));
        foreach (var network in new[] { "::ffff:203.0.113.7", "::ffff:203.0.113.7/128", "::ffff:203.0.113.0/120", "::ffff:0.0.0.0/96" })
            Assert.Equal(new[] { "203.0.113.7" }, await Ips("ip=" + Uri.EscapeDataString(network)));
        Assert.Empty(await Ips("ip=" + Uri.EscapeDataString("::ffff:198.51.100.0/120")));
        Assert.Equal(new[] { "2001:db8:1:2::20", "2001:db8:1:2::10" }, await Ips("ip=" + Uri.EscapeDataString("2001:db8:1:2::99/64")));
        Assert.Empty(await Ips("ip=" + Uri.EscapeDataString("2001:db8:1:3::/64")));
        Assert.Equal(new[] { "2001:db8:1:2::20" }, await Ips("path=/plugins"));
        Assert.Equal(new[] { "2001:db8:1:2::20" },
            await Ips($"since={Uri.EscapeDataString(At(25))}&until={Uri.EscapeDataString(At(15))}"));

        var first = JObject.Parse(await admin.GetStringAsync("/api/v1/admin/access-log?limit=1"));
        Assert.True(first.Value<bool>("hasMore"));
        var next = first.Value<string>("nextCursor");
        Assert.Equal(new[] { "2001:db8:1:2::20", "2001:db8:1:2::10" }, await Ips("before=" + next));

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/v1/admin/access-log?ip=not-an-ip")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/v1/admin/access-log?ip=203.0.113.7/33")).StatusCode);
        foreach (var ip in new[] { "10/8", "172.16/12", "192.168/16", "192.168.010.001" })
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/v1/admin/access-log?ip=" + Uri.EscapeDataString(ip))).StatusCode);
        using var user = tester.CreateHttpClient().SetBasicAuth("user@access-log.test", Password);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/v1/admin/access-log")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await tester.CreateHttpClient().GetAsync("/api/v1/admin/access-log")).StatusCode);

        // Retention follows nginx's time: a row older than the window goes, the recent ones stay.
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.ExecuteAsync("""
            INSERT INTO nginx_access_log (logged_at, ip, host, method, path, status, bytes, user_agent)
            VALUES (now() - interval '8 days', '192.0.2.1', 'h', 'GET', '/', 200, 0, '')
            """);
        Assert.Equal(1, await receiver.Purge(CancellationToken.None));
        Assert.Equal(3, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM nginx_access_log"));
    }

    [Fact]
    public async Task PurgesAutomaticallyWithoutAListener()
    {
        await using var tester = Create();
        tester.ReuseDatabase = false;
        tester.ConfigureApplication = app =>
        {
            app.Configuration["ACCESSLOG_SYSLOG_PORT"] = null;
            app.Configuration["ACCESSLOG_RETENTION_DAYS"] = "3";
        };
        tester.ConfigureServices = services =>
        {
            // Start only the database so rows can be seeded before the receiver's first retention pass.
            foreach (var service in services.Where(s => s.ServiceType == typeof(IHostedService) &&
                         s.ImplementationType != typeof(DatabaseStartupHostedService)).ToArray())
                services.Remove(service);
        };
        await tester.Start();
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.ExecuteAsync("""
            INSERT INTO nginx_access_log (logged_at, ip, host, method, path, status, bytes, user_agent)
            VALUES (now() - interval '4 days', '192.0.2.1', 'h', 'GET', '/expired', 200, 0, ''),
                   (now() - interval '2 days', '192.0.2.2', 'h', 'GET', '/recent', 200, 0, '')
            """);

        var receiver = tester.GetService<AccessLogHostedService>();
        await receiver.StartAsync(CancellationToken.None);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receiver.Bound.WaitAsync(TimeSpan.FromSeconds(10)));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM nginx_access_log WHERE path = '/expired'") != 0)
                await Task.Delay(25, deadline.Token);

            Assert.Equal(new[] { "/recent" }, await conn.QueryAsync<string>("SELECT path FROM nginx_access_log"));
        }
        finally
        {
            await receiver.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void RefusesAnInvalidConfiguration()
    {
        AccessLogHostedService Build(string key, string value) => new(null!,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build(),
            NullLogger<AccessLogHostedService>.Instance);
        Assert.Throws<ConfigurationException>(() => Build("ACCESSLOG_SYSLOG_PORT", "70000"));
        Assert.Throws<ConfigurationException>(() => Build("ACCESSLOG_SYSLOG_PORT", "-1"));
        Assert.Throws<ConfigurationException>(() => Build("ACCESSLOG_RETENTION_DAYS", "0"));
        Assert.Throws<ConfigurationException>(() => Build("ACCESSLOG_RETENTION_DAYS", "91"));
        Assert.Equal(7, Build("ACCESSLOG_SYSLOG_PORT", "").Days);
    }
}
