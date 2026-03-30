using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.PublicTests;

public class ErrorPageTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    [Fact]
    public async Task UnknownRoute_WithHtmlAccept_ReturnsCustom404Page()
    {
        await using var tester = await Start();
        var client = tester.CreateHttpClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        var response = await client.GetAsync("/this-route-does-not-exist");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("404", body, StringComparison.Ordinal);
        Assert.Contains("could not be found", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownRoute_WithJsonAccept_ReturnsPlain404()
    {
        await using var tester = await Start();
        var client = tester.CreateHttpClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await client.GetAsync("/this-route-does-not-exist");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("could not be found", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExceptionHandler_WithHtmlAccept_ReturnsCustom500Page()
    {
        await using var tester = Create();
        tester.ConfigureApplication = app => app.MapGet("/throw/500", (HttpContext _) => throw new InvalidOperationException("Test 500"));
        await tester.Start();

        var client = tester.CreateHttpClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        var response = await client.GetAsync("/throw/500");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("500", body, StringComparison.Ordinal);
        Assert.Contains("An unexpected server error occurred", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExceptionHandler_WithJsonAccept_ReturnsPlain500()
    {
        await using var tester = Create();
        tester.ConfigureApplication = app => app.MapGet("/throw/500", (HttpContext _) => throw new InvalidOperationException("Test 500"));
        await tester.Start();

        var client = tester.CreateHttpClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await client.GetAsync("/throw/500");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("An unexpected server error occurred", body, StringComparison.OrdinalIgnoreCase);
    }
}


