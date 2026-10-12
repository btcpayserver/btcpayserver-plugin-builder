using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PluginBuilder.HostedServices;
using Microsoft.Extensions.Logging.Abstractions;
using PluginBuilder.DataModels;
using PluginBuilder.Services;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests;

public class TurnstileVerifierTests
{
    [Theory]
    [InlineData(null, "secret")]
    [InlineData("site", null)]
    [InlineData(" ", "secret")]
    public async Task WithoutBothKeysItIsOffAndNeverCallsCloudflare(string? siteKey, string? secret)
    {
        var handler = new StubHandler(_ => Json("{\"success\":false}"));
        var verifier = Verifier(siteKey, secret, handler);

        Assert.False(verifier.Enabled);
        Assert.True(await verifier.VerifyAsync(null, null, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task APassingTokenIsVerifiedServerSideWithTheSecret()
    {
        var handler = new StubHandler(_ => Json("{\"success\":true,\"error-codes\":[]}"));
        var verifier = Verifier("site-key", "secret-key", handler);

        Assert.True(verifier.Enabled);
        Assert.Equal("site-key", verifier.SiteKey);
        Assert.True(await verifier.VerifyAsync("token-from-widget", "203.0.113.7", CancellationToken.None));

        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(TurnstileVerifier.SiteVerifyUrl, request.RequestUri);
        Assert.Contains("secret=secret-key", body);
        Assert.Contains("response=token-from-widget", body);
        Assert.Contains("remoteip=203.0.113.7", body);
    }

    [Theory]
    [InlineData("{\"success\":false,\"error-codes\":[\"invalid-input-response\"]}")]
    [InlineData("{\"success\":\"true\"}")]
    [InlineData("[]")]
    [InlineData("not json")]
    public async Task AnythingButSuccessTrueIsARefusal(string reply)
    {
        var verifier = Verifier("site", "secret", new StubHandler(_ => Json(reply)));
        Assert.False(await verifier.VerifyAsync("token", null, CancellationToken.None));
    }

    [Fact]
    public async Task AnUnreachableOrFailingCloudflareIsARefusal()
    {
        Assert.False(await Verifier("site", "secret", new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)))
            .VerifyAsync("token", null, CancellationToken.None));
        Assert.False(await Verifier("site", "secret", new StubHandler(_ => throw new HttpRequestException("down")))
            .VerifyAsync("token", null, CancellationToken.None));
        // HttpClient reports its own timeout as a cancellation the caller did not ask for.
        Assert.False(await Verifier("site", "secret", new StubHandler(_ => throw new TaskCanceledException("timeout")))
            .VerifyAsync("token", null, CancellationToken.None));
    }

    [Fact]
    public async Task AMissingOrOversizedTokenIsRefusedWithoutARequest()
    {
        var handler = new StubHandler(_ => Json("{\"success\":true}"));
        var verifier = Verifier("site", "secret", handler);

        Assert.False(await verifier.VerifyAsync(null, null, CancellationToken.None));
        Assert.False(await verifier.VerifyAsync(" ", null, CancellationToken.None));
        Assert.False(await verifier.VerifyAsync(new string('a', 2049), null, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TheCallersCancellationIsNotSwallowed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var verifier = Verifier("site", "secret", new StubHandler(_ => throw new TaskCanceledException()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verifier.VerifyAsync("token", null, cts.Token));
    }

    private static TurnstileVerifier Verifier(string? siteKey, string? secret, StubHandler handler)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TURNSTILE_SITEKEY"] = siteKey,
            ["TURNSTILE_SECRET"] = secret
        }).Build();
        return new TurnstileVerifier(configuration, new StubFactory(handler), NullLogger<TurnstileVerifier>.Instance);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            cancellationToken.ThrowIfCancellationRequested();
            return respond(request);
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(HttpClientNames.Turnstile, name);
            return new HttpClient(handler, disposeHandler: false);
        }
    }
}

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class TurnstileRegistrationTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    [Fact]
    public async Task WhenOnRegistrationNeedsAPassingCheck()
    {
        var fake = new FakeVerifier();
        await using var tester = Create("TurnstileOn");
        tester.ReuseDatabase = false;
        tester.ConfigureServices = services =>
        {
            WithoutBackgroundServices(services);
            services.Replace(ServiceDescriptor.Singleton<ITurnstileVerifier>(fake));
        };
        await tester.Start();
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        using var client = BrowserClient(tester);

        var page = await (await client.GetAsync("/register")).Content.ReadAsStringAsync();
        Assert.Contains("class=\"cf-turnstile\" data-sitekey=\"test-site-key\"", page);
        Assert.Contains("https://challenges.cloudflare.com/turnstile/v0/api.js", page);

        var refused = $"turnstile-refused-{Guid.NewGuid():N}@example.com";
        using (var response = await client.PostAsync("/register", Form(refused, AntiforgeryToken(page), null)))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("Please complete the human check and try again.", await response.Content.ReadAsStringAsync());
        }
        Assert.False(await UserExists(conn, refused));

        var wrong = $"turnstile-wrong-{Guid.NewGuid():N}@example.com";
        using (var response = await client.PostAsync("/register", Form(wrong, AntiforgeryToken(page), "bad-token")))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(await UserExists(conn, wrong));

        var accepted = $"turnstile-passed-{Guid.NewGuid():N}@example.com";
        using (var response = await client.PostAsync("/register", Form(accepted, AntiforgeryToken(page), FakeVerifier.PassingToken)))
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.True(await UserExists(conn, accepted));
        Assert.Equal(new[] { null, "bad-token", FakeVerifier.PassingToken }, fake.Tokens);
    }

    [Fact]
    public async Task WhenOffRegistrationWorksAsBeforeAndShowsNoWidget()
    {
        await using var tester = Create("TurnstileOff");
        tester.ReuseDatabase = false;
        tester.ConfigureServices = WithoutBackgroundServices;
        await tester.Start();
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        Assert.False(tester.GetService<ITurnstileVerifier>().Enabled);
        using var client = BrowserClient(tester);

        var page = await (await client.GetAsync("/register")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("cf-turnstile", page);
        Assert.DoesNotContain("challenges.cloudflare.com", page);

        var email = $"turnstile-off-{Guid.NewGuid():N}@example.com";
        using var response = await client.PostAsync("/register", Form(email, AntiforgeryToken(page), null));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.True(await UserExists(conn, email));
    }

    // Registration needs only the database: drop the other hosted services (storage, builds), as the admin API tests do.
    private static void WithoutBackgroundServices(IServiceCollection services)
    {
        foreach (var service in services.Where(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType != typeof(DatabaseStartupHostedService)).ToArray())
            services.Remove(service);
    }

    private sealed class FakeVerifier : ITurnstileVerifier
    {
        public const string PassingToken = "passing-token";
        public List<string?> Tokens { get; } = new();
        public bool Enabled => true;
        public string? SiteKey => "test-site-key";

        public Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken cancellationToken)
        {
            Tokens.Add(string.IsNullOrEmpty(token) ? null : token);
            return Task.FromResult(token == PassingToken);
        }
    }

    private static FormUrlEncodedContent Form(string email, string antiforgeryToken, string? turnstileToken)
    {
        var fields = new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = "123456",
            ["ConfirmPassword"] = "123456",
            ["__RequestVerificationToken"] = antiforgeryToken
        };
        if (turnstileToken is not null)
            fields[TurnstileVerifier.ResponseField] = turnstileToken;
        return new FormUrlEncodedContent(fields);
    }

    private static Task<bool> UserExists(Npgsql.NpgsqlConnection conn, string email) =>
        conn.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM \"AspNetUsers\" WHERE \"Email\" = @email)", new { email });

    private static HttpClient BrowserClient(ServerTester tester) =>
        new(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer() })
        {
            BaseAddress = new Uri(tester.WebApp.Urls.First(), UriKind.Absolute)
        };

    private static string AntiforgeryToken(string html)
    {
        var input = Regex.Match(html, "<input\\b(?=[^>]*\\bname=\"__RequestVerificationToken\")[^>]*>", RegexOptions.IgnoreCase);
        Assert.True(input.Success, "HTML did not contain an antiforgery token input.");
        var value = Regex.Match(input.Value, "\\bvalue=\"([^\"]+)\"", RegexOptions.IgnoreCase);
        Assert.True(value.Success, "Antiforgery token input did not contain a value.");
        return WebUtility.HtmlDecode(value.Groups[1].Value);
    }
}
