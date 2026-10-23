using System.Net;
using Newtonsoft.Json.Linq;
using PluginBuilder.DataModels;
using PluginBuilder.Services;
using Xunit;

namespace PluginBuilder.Tests;

public class ExternalAccountVerificationServiceTests
{
    private const string Token = "user-bound-proof-token";
    private const string Url = "https://gist.github.com/proofowner/a1b2c3";
    private const long OwnerId = 5_000_000_001;

    [Theory]
    [InlineData(Url)]
    [InlineData(Url + "/")]
    [InlineData(Url + "?file=proof.txt#file-proof-txt")]
    [InlineData(Url + "?note=/../def456#../proof")]
    [InlineData("HTTPS://GIST.GITHUB.COM:443/proofowner/a1b2c3")]
    public async Task FreshProofReturnsTheApiIdentityAndUsesTheFixedGistEndpoint(string url)
    {
        using var handler = new StubHandler((_, _) => Json(Proof().ToString()));
        var service = Service(handler);

        var result = await service.VerifyGistToken(url, Token);

        Assert.Equal(new VerifiedGithubIdentity(OwnerId, "ProofOwner"), result);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(new Uri("https://api.github.com/gists/a1b2c3"), request.Uri);
    }

    [Theory]
    [MemberData(nameof(UnverifiableProofs))]
    public async Task NeitherModeAcceptsAnUnverifiableResponse(string scenario, string body)
    {
        using var handler = new StubHandler((_, _) => Json(body));
        var service = Service(handler);
        Assert.True(await service.VerifyGistToken(Url, Token) is null, scenario);
        Assert.True(await service.RecoverGistIdentity(Url, Token) is null, scenario);
    }

    public static IEnumerable<object[]> UnverifiableProofs()
    {
        foreach (var body in new[] { "not json", "[", "[]", "null" })
            yield return [$"invalid JSON object: {body}", body];
        yield return Case("missing id", proof => ((JObject)proof["owner"]!).Remove("id"));
        foreach (var id in new[] { "null", "0", "-1", "\"5000000001\"", "1.5", "true", "{}", "9223372036854775808" })
            yield return Case($"invalid id: {id}", proof => proof["owner"]!["id"] = JToken.Parse(id));
        yield return Case("missing owner", proof => proof.Remove("owner"));
        yield return Case("null owner", proof => proof["owner"] = null);
        yield return Case("missing login", proof => ((JObject)proof["owner"]!).Remove("login"));
        yield return Case("empty login", proof => proof["owner"]!["login"] = "");
        yield return Case("missing files", proof => proof.Remove("files"));
        yield return Case("empty files", proof => proof["files"] = new JObject());
        yield return Case("missing content", proof => ((JObject)proof["files"]!["proof.txt"]!).Remove("content"));
        yield return Case("null content", proof => proof["files"]!["proof.txt"]!["content"] = null);
        yield return Case("another user's token", proof => proof["files"]!["proof.txt"]!["content"] = "a-different-token");

        static object[] Case(string scenario, Action<JObject> mutate)
        {
            var proof = Proof();
            mutate(proof);
            return [scenario, proof.ToString()];
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyRecoveryAcceptsARenamedGistOwner(bool recover)
    {
        using var handler = new StubHandler((_, _) => Json(Proof().ToString()));
        var service = Service(handler);
        const string oldUrl = "https://gist.github.com/former-login/a1b2c3";

        var result = await Verify(service, recover, oldUrl);

        if (recover)
            Assert.Equal(new VerifiedGithubIdentity(OwnerId, "ProofOwner"), result);
        else
            Assert.Null(result);
        Assert.Equal(new Uri("https://api.github.com/gists/a1b2c3"), Assert.Single(handler.Requests).Uri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a URL")]
    [InlineData("http://gist.github.com/proofowner/a1b2c3")]
    [InlineData("https://example.com/proofowner/a1b2c3")]
    [InlineData("https://gist.github.com.example.com/proofowner/a1b2c3")]
    [InlineData("https://example.com/https://gist.github.com/proofowner/a1b2c3")]
    [InlineData("https://user@gist.github.com/proofowner/a1b2c3")]
    [InlineData("https://gist.github.com:444/proofowner/a1b2c3")]
    [InlineData("https://gist.github.com/proofowner")]
    [InlineData("https://gist.github.com//a1b2c3")]
    [InlineData("https://gist.github.com/proofowner/not-hex")]
    [InlineData("https://gist.github.com/proofowner/a1b2c3/extra")]
    [InlineData(Url + "/../def456")]
    [InlineData(Url + "/%2e%2e/def456")]
    [InlineData(Url + "/.%2e/def456")]
    [InlineData(Url + "/%2E./def456")]
    [InlineData("https://gist.github.com/proofowner/./a1b2c3")]
    [InlineData("https://gist.github.com/proofowner/ignored/../a1b2c3")]
    [InlineData("https://gist.github.com/proofowner/a1b2c3\\..\\def456")]
    public async Task InvalidOrNormalizedProofUrlsAreRejectedWithoutAnHttpRequest(string url)
    {
        using var handler = new StubHandler((_, _) => Json(Proof().ToString()));
        await AssertRejected(handler, url);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task MissingBindingTokensAreRejectedWithoutAnHttpRequest(string? token)
    {
        using var handler = new StubHandler((_, _) => Json(Proof().ToString()));
        await AssertRejected(handler, token: token!);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task FailedHttpResponsesDoNotVerifyEitherIdentity(HttpStatusCode status)
    {
        using var handler = new StubHandler((_, _) => new HttpResponseMessage(status));
        await AssertRejected(handler);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NetworkFailureAndHttpClientTimeoutDoNotVerifyEitherIdentity(bool timeout)
    {
        using var handler = new StubHandler((_, _) => throw (timeout
            ? new TaskCanceledException("HTTP client timeout")
            : new HttpRequestException("GitHub unavailable")));
        await AssertRejected(handler);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellationDuringTheRequestIsPropagated(bool recover)
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new StubHandler((_, requestCancellation) =>
        {
            cancellation.Cancel();
            requestCancellation.ThrowIfCancellationRequested();
            return Json(Proof().ToString());
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Verify(Service(handler), recover, Url, cancellation.Token));
        Assert.Single(handler.Requests);
    }

    private static Task<VerifiedGithubIdentity?> Verify(ExternalAccountVerificationService service, bool recover,
        string url, CancellationToken cancellationToken = default) => recover
        ? service.RecoverGistIdentity(url, Token, cancellationToken)
        : service.VerifyGistToken(url, Token, cancellationToken);

    private static async Task AssertRejected(StubHandler handler, string url = Url, string token = Token)
    {
        var service = Service(handler);
        Assert.Null(await service.VerifyGistToken(url, token));
        Assert.Null(await service.RecoverGistIdentity(url, token));
    }

    private static JObject Proof() => JObject.Parse("""
        {
          "owner": { "id": 5000000001, "login": "ProofOwner" },
          "files": { "proof.txt": { "content": "user-bound-proof-token" } }
        }
        """);

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
    };

    private static ExternalAccountVerificationService Service(StubHandler handler) => new(new StubFactory(handler));

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri? Uri)> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri));
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request, cancellationToken));
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(HttpClientNames.GitHub, name);
            return new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.github.com/") };
        }
    }
}
