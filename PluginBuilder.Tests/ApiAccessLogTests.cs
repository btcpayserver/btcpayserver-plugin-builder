using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PluginBuilder.Filters;
using Xunit;

namespace PluginBuilder.Tests;

public class ApiAccessLogTests
{
    [Theory]
    [InlineData(401, "", "none")]
    [InlineData(401, "Basic secret-credentials", "basic")]
    [InlineData(401, "Bearer secret-token", "bearer")]
    [InlineData(403, "Unknown secret-credentials", "other")]
    public async Task DenialIncludesContextWithoutCredentials(int status, string authorization, string credentialType)
    {
        var log = new CaptureLogger();
        var http = NewContext();
        http.Request.Headers.Authorization = authorization;
        http.Request.QueryString = new QueryString("?token=secret-query");
        http.Request.Headers.Cookie = "session=secret-cookie";
        http.Request.Body = new MemoryStream("secret-body"u8.ToArray());
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "account-123")], "test"));
        var middleware = new ApiAccessLogMiddleware(context =>
        {
            context.Response.StatusCode = status;
            return Task.CompletedTask;
        }, log);

        await middleware.InvokeAsync(http);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(status, entry.Fields["StatusCode"]);
        Assert.Equal("192.0.2.10", entry.Fields["ClientIp"]);
        Assert.Equal("GET", entry.Fields["Method"]);
        Assert.Equal("/api/v1/admin/events", entry.Fields["Path"]);
        Assert.Equal(credentialType, entry.Fields["CredentialType"]);
        Assert.Equal("account-123", entry.Fields["UserId"]);
        Assert.Equal("request-123", entry.Fields["RequestId"]);
        Assert.Equal("test-client/1.0", entry.Fields["UserAgent"]);
        Assert.DoesNotContain("secret-", entry.Message);
        Assert.Equal(0, http.Request.Body.Position);
    }

    [Theory]
    [InlineData("/api/v1/admin/events", 200)]
    [InlineData("/api/v1/admin/events", 404)]
    [InlineData("/api/v1/plugins/example/builds", 503)]
    [InlineData("/errors/401", 401)]
    [InlineData("/login", 403)]
    public async Task OtherResponsesStayQuiet(string path, int status)
    {
        var log = new CaptureLogger();
        var http = NewContext();
        http.Request.Path = path;
        var middleware = new ApiAccessLogMiddleware(context =>
        {
            context.Response.StatusCode = status;
            return Task.CompletedTask;
        }, log);

        await middleware.InvokeAsync(http);

        Assert.Empty(log.Entries);
        Assert.Equal(status, http.Response.StatusCode);
    }

    [Fact]
    public async Task FieldsAreBoundedAndCannotInjectLogLines()
    {
        var log = new CaptureLogger();
        var http = NewContext();
        http.Connection.RemoteIpAddress = null;
        http.Request.Path = "/api/v1/" + new string('p', 1000);
        http.Request.Headers.UserAgent = "client\r\n\t\u2028\u2029\u202e" + new string('a', 1000);
        var middleware = new ApiAccessLogMiddleware(context =>
        {
            context.Response.StatusCode = 401;
            return Task.CompletedTask;
        }, log);

        await middleware.InvokeAsync(http);

        var entry = Assert.Single(log.Entries);
        Assert.Equal("unknown", entry.Fields["ClientIp"]);
        Assert.Equal("-", entry.Fields["UserId"]);
        Assert.Equal(259, Assert.IsType<string>(entry.Fields["Path"]).Length);
        Assert.Equal(163, Assert.IsType<string>(entry.Fields["UserAgent"]).Length);
        Assert.DoesNotContain(entry.Message, c => char.IsControl(c) || c is '\u2028' or '\u2029' or '\u202e');
    }

    [Fact]
    public async Task ForwardedAddressAndOriginalPathSurviveErrorPageReexecution()
    {
        var log = new CaptureLogger();
        var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        await using var serviceScope = services;
        var app = new ApplicationBuilder(services);
        var forwarding = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor };
        forwarding.KnownProxies.Add(IPAddress.Parse("192.0.2.10"));
        app.UseForwardedHeaders(forwarding);
        app.UseStatusCodePagesWithReExecute("/errors/{0}");
        app.Use(next => new ApiAccessLogMiddleware(next, log).InvokeAsync);
        app.Run(context =>
        {
            context.Response.StatusCode = 401;
            if (context.Request.Path.StartsWithSegments("/errors"))
                context.Response.ContentType = "text/plain";
            return Task.CompletedTask;
        });
        var http = NewContext();
        http.RequestServices = services;
        http.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
        http.Request.Headers["X-Forwarded-For"] = "198.51.100.7";

        await app.Build()(http);

        var entry = Assert.Single(log.Entries);
        Assert.Equal("198.51.100.7", entry.Fields["ClientIp"]);
        Assert.Equal("/api/v1/admin/events", entry.Fields["Path"]);
        Assert.Equal(401, http.Response.StatusCode);
    }

    private static DefaultHttpContext NewContext()
    {
        var http = new DefaultHttpContext { TraceIdentifier = "request-123" };
        http.Request.Method = "GET";
        http.Request.Path = "/api/v1/admin/events";
        http.Request.Headers.UserAgent = "test-client/1.0";
        http.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:192.0.2.10");
        return http;
    }

    private sealed class CaptureLogger : ILogger<ApiAccessLogMiddleware>
    {
        public List<Entry> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add(new Entry(logLevel, formatter(state, exception),
                ((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary(pair => pair.Key, pair => pair.Value)));
    }

    private sealed record Entry(LogLevel Level, string Message, Dictionary<string, object?> Fields);
}
