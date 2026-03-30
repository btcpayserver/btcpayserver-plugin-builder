using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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
        Assert.False(string.IsNullOrWhiteSpace(body));
        using var json = JsonDocument.Parse(body);
        Assert.Equal(404, json.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Not Found", json.RootElement.GetProperty("title").GetString());
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
        Assert.False(string.IsNullOrWhiteSpace(body));
        using var json = JsonDocument.Parse(body);
        Assert.Equal(500, json.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Internal Server Error", json.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Non500Status_WithHtmlAccept_UsesGenericErrorView()
    {
        await using var tester = Create();
        tester.ConfigureApplication = app => app.MapGet("/status/503", () => Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
        await tester.Start();

        var client = tester.CreateHttpClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        var response = await client.GetAsync("/status/503");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("503", body, StringComparison.Ordinal);
        Assert.Contains("An error occurred while processing your request", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExceptionHandler_PostRequest_WithHtmlAccept_RendersCustom500Page()
    {
        await using var tester = Create();
        tester.ConfigureApplication = app => app.MapPost("/throw/post-500", (HttpContext _) => throw new InvalidOperationException("Test POST 500"));
        await tester.Start();

        var client = tester.CreateHttpClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/throw/post-500", content);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("500", body, StringComparison.Ordinal);
        Assert.Contains("An unexpected server error occurred", body, StringComparison.OrdinalIgnoreCase);
    }
}
