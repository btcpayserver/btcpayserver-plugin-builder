using System.Net;
using System.Net.Http.Headers;
using System.Text;
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
        Assert.Contains("404 - Page not found", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("It doesn't exist", body, StringComparison.OrdinalIgnoreCase);
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
        Assert.Equal(string.Empty, body);
    }

    [Theory]
    [InlineData("/errors/404", HttpStatusCode.NotFound, "text/html", "404 - Page not found")]
    [InlineData("/errors/404", HttpStatusCode.NotFound, "application/json", null)]
    [InlineData("/this-route-does-not-exist", HttpStatusCode.NotFound, "text/html", "404 - Page not found")]
    [InlineData("/this-route-does-not-exist", HttpStatusCode.NotFound, "application/json", null)]
    [InlineData("/forgotpassword", HttpStatusCode.BadRequest, "text/html", "CSRF token validation failed.")]
    [InlineData("/forgotpassword", HttpStatusCode.BadRequest, "application/json", null)]
    public async Task MalformedFormKey_PreservesErrorResponse(string path, HttpStatusCode expectedStatus, string accept, string? expectedContent)
    {
        await using var tester = Create();
        tester.ReuseDatabase = false;
        await tester.Start();

        using var client = tester.CreateHttpClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));

        using var content = new StringContent("x%5B=value", Encoding.UTF8, "application/x-www-form-urlencoded");
        using var response = await client.PostAsync(path, content);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(expectedStatus, response.StatusCode);
        if (expectedContent is null)
            Assert.Equal(string.Empty, body);
        else
            Assert.Contains(expectedContent, body, StringComparison.OrdinalIgnoreCase);
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
        Assert.Contains("500 - Internal Server Error", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Whoops, something really went wrong", body, StringComparison.OrdinalIgnoreCase);
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
        Assert.Equal(string.Empty, body);
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
        Assert.Contains("503 - Service Unavailable", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("A generic error occurred (HTTP Code: 503)", body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{}", "application/json")]
    [InlineData("x%5B=value", "application/x-www-form-urlencoded")]
    public async Task ExceptionHandler_PostRequest_WithHtmlAccept_RendersCustom500Page(string payload, string contentType)
    {
        await using var tester = Create();
        tester.ReuseDatabase = false;
        tester.ConfigureApplication = app => app.MapPost("/throw/post-500", (HttpContext _) => throw new InvalidOperationException("Test POST 500"));
        await tester.Start();

        var client = tester.CreateHttpClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        using var content = new StringContent(payload, Encoding.UTF8, contentType);
        var response = await client.PostAsync("/throw/post-500", content);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("500 - Internal Server Error", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Whoops, something really went wrong", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SpecialRoute_WithHtmlAccept_ReturnsDedicated406Page()
    {
        await using var tester = await Start();
        var client = tester.CreateHttpClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        var response = await client.GetAsync("/errors/406");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotAcceptable, response.StatusCode);
        Assert.Contains("406 - Not Acceptable", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("can't serve you", body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(403, HttpStatusCode.Forbidden, "403 - Denied", "It's not your business")]
    [InlineData(429, HttpStatusCode.TooManyRequests, "429 - Too Many Requests", "Please send requests slower")]
    [InlineData(502, HttpStatusCode.BadGateway, "502 - Bad Gateway", "found a bad one")]
    public async Task SpecialRoute_WithHtmlAccept_ReturnsDedicatedPages(int statusCode, HttpStatusCode expectedStatus, string expectedTitle, string expectedCopy)
    {
        await using var tester = await Start();
        var client = tester.CreateHttpClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        var response = await client.GetAsync($"/errors/{statusCode}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Contains(expectedTitle, body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(expectedCopy, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SpecialRoute_WithJsonAccept_ReturnsPlain406()
    {
        await using var tester = await Start();
        var client = tester.CreateHttpClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await client.GetAsync("/errors/406");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotAcceptable, response.StatusCode);
        Assert.Equal(string.Empty, body);
    }

    [Fact]
    public async Task OutOfRangeErrorRoute_WithHtmlAccept_DoesNotHitErrorControllerAction()
    {
        await using var tester = await Start();
        var client = tester.CreateHttpClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        var response = await client.GetAsync("/errors/399");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("404 - Page not found", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("399 -", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ForgotPassword_WithoutAntiforgeryToken_ShowsCsrfDetails()
    {
        await using var tester = await Start();
        var client = tester.CreateHttpClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = "test@example.com"
        });
        var response = await client.PostAsync("/forgotpassword", content);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("400 - Bad Request", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CSRF token validation failed.", body, StringComparison.OrdinalIgnoreCase);
    }
}
