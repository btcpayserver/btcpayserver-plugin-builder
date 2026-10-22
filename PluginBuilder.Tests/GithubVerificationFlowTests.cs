using System.Net;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json.Linq;
using PluginBuilder.DataModels;
using PluginBuilder.HostedServices;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class GithubVerificationFlowTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    private const string VerificationPath = "/account/VerifyGithubAccount";
    private const string GistUrl = "https://gist.github.com/old-login/abcdef123456";
    private const string Password = "123456";

    [Fact]
    public async Task ANewProofUsesTheSignedInAccountTokenAndTheApiOwnerIdentity()
    {
        var github = new GithubHandler();
        await using var tester = await StartServer(github);
        var email = $"github-new-{Guid.NewGuid():N}@example.com";
        var userId = await tester.CreateFakeUserAsync(email, Password, githubVerified: false);
        using var browser = HttpTestHelpers.CreateBrowser(tester);
        await HttpTestHelpers.LogIn(browser, email, Password);
        var page = await Page(browser, VerificationPath);
        Assert.Equal(userId, HttpTestHelpers.InputValue(page, "Token"));

        // A client-controlled token cannot make a gist proving another builder account valid.
        github.Reply = () => Proof("OLD-LOGIN", 4294967296, "someone-else");
        using (var refused = await Submit(browser, page, GistUrl, "someone-else"))
        {
            Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
            page = await refused.Content.ReadAsStringAsync();
            Assert.Contains($"<span id=\"token\" class=\"fs-3\">{userId}</span>", page);
        }
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        Assert.Null(await conn.GetGithubUserId(userId));
        Assert.False(await conn.IsGithubAccountVerified(userId));

        // Casing and the numeric ID must come from GitHub, not from the submitted URL or token.
        github.Reply = () => Proof("OLD-LOGIN", 4294967296, userId);
        using (var accepted = await Submit(browser, page, GistUrl, "someone-else"))
        {
            Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
            Assert.Equal("/account/details", new Uri(browser.BaseAddress!, accepted.Headers.Location!).AbsolutePath);
        }
        await AssertStoredIdentity(tester, userId, 4294967296, "OLD-LOGIN", GistUrl);
        Assert.Equal(2, github.Requests.Count);
        Assert.All(github.Requests, uri => Assert.Equal("https://api.github.com/gists/abcdef123456", uri.AbsoluteUri));

        var details = await Page(browser, "/account/details");
        Assert.DoesNotContain("Refresh GitHub verification", details);
        Assert.Contains("OLD-LOGIN", details);
        Assert.Equal(2, github.Requests.Count);
    }

    [Fact]
    public async Task RefreshingALegacyProofRecoversTheIdAfterAGithubRename()
    {
        var github = new GithubHandler();
        await using var tester = await StartServer(github);
        var email = $"github-legacy-{Guid.NewGuid():N}@example.com";
        var userId = await tester.CreateFakeUserAsync(email, Password);
        await StoreLegacyProof(tester, userId);
        github.Reply = () => Proof("renamed-owner", 7654321, userId);
        using var browser = HttpTestHelpers.CreateBrowser(tester);
        await HttpTestHelpers.LogIn(browser, email, Password);

        var details = await Page(browser, "/account/details");
        Assert.Contains("Refresh GitHub verification", details);
        Assert.Empty(github.Requests);

        using var refreshed = await browser.GetAsync(VerificationPath);
        Assert.Equal(HttpStatusCode.Redirect, refreshed.StatusCode);
        await AssertStoredIdentity(tester, userId, 7654321, "renamed-owner", GistUrl);
        Assert.Single(github.Requests);
        Assert.DoesNotContain("Refresh GitHub verification", await Page(browser, "/account/details"));
    }

    [Fact]
    public async Task ADeletedLegacyGistShowsTheProofFormAndKeepsTheExistingVerification()
    {
        var github = new GithubHandler { Reply = () => new HttpResponseMessage(HttpStatusCode.NotFound) };
        await using var tester = await StartServer(github);
        var email = $"github-deleted-{Guid.NewGuid():N}@example.com";
        var userId = await tester.CreateFakeUserAsync(email, Password);
        await StoreLegacyProof(tester, userId);
        using var browser = HttpTestHelpers.CreateBrowser(tester);
        await HttpTestHelpers.LogIn(browser, email, Password);

        var form = await Page(browser, VerificationPath);
        Assert.Equal(userId, HttpTestHelpers.InputValue(form, "Token"));
        Assert.Contains("name=\"GistUrl\"", form);
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        Assert.Null(await conn.GetGithubUserId(userId));
        Assert.True(await conn.IsGithubAccountVerified(userId));
        Assert.Equal(GistUrl, await conn.ExecuteScalarAsync<string>(
            "SELECT \"GithubGistUrl\" FROM \"AspNetUsers\" WHERE \"Id\" = @userId", new { userId }));
        Assert.Equal("old-login", (await conn.GetAccountDetailSettings(userId))!.Github);
        Assert.Single(github.Requests);
    }

    [Fact]
    public async Task OpeningVerificationClearsOnlyTheUnprovenGithubHandle()
    {
        var github = new GithubHandler();
        await using var tester = await StartServer(github);
        var email = $"github-unproven-{Guid.NewGuid():N}@example.com";
        var userId = await tester.CreateFakeUserAsync(email, Password, githubVerified: false);
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.ExecuteAsync("""
            UPDATE "AspNetUsers" SET "AccountDetail" = '{"github":"self-declared","twitter":"keep-this"}'::jsonb
            WHERE "Id" = @userId
            """, new { userId });
        using var browser = HttpTestHelpers.CreateBrowser(tester);
        await HttpTestHelpers.LogIn(browser, email, Password);

        var form = await Page(browser, VerificationPath);
        Assert.Equal(userId, HttpTestHelpers.InputValue(form, "Token"));
        var settings = (await conn.GetAccountDetailSettings(userId))!;
        Assert.Null(settings.Github);
        Assert.Equal("keep-this", settings.Twitter);
        Assert.False(await conn.IsGithubAccountVerified(userId));
        Assert.Null(await conn.GetGithubUserId(userId));
        Assert.Empty(github.Requests);
    }

    [Fact]
    public async Task AKnownIdentityDoesNotDependOnTheGistStillBeingAvailable()
    {
        var github = new GithubHandler { Reply = () => throw new InvalidOperationException("Known identities must not call GitHub") };
        await using var tester = await StartServer(github);
        var email = $"github-known-{Guid.NewGuid():N}@example.com";
        var userId = await tester.CreateFakeUserAsync(email, Password, githubVerified: false);
        await using (var conn = await tester.GetService<DBConnectionFactory>().Open())
            await conn.VerifyGithubAccount(userId, GistUrl, new VerifiedGithubIdentity(7654321, "known-owner"));
        using var browser = HttpTestHelpers.CreateBrowser(tester);
        await HttpTestHelpers.LogIn(browser, email, Password);

        using var refreshed = await browser.GetAsync(VerificationPath);
        Assert.Equal(HttpStatusCode.Redirect, refreshed.StatusCode);
        Assert.DoesNotContain("Refresh GitHub verification", await Page(browser, "/account/details"));
        await AssertStoredIdentity(tester, userId, 7654321, "known-owner", GistUrl);
        Assert.Empty(github.Requests);
    }

    private async Task<ServerTester> StartServer(GithubHandler github)
    {
        var tester = Create(nameof(GithubVerificationFlowTests));
        tester.ReuseDatabase = false;
        tester.ConfigureServices = services =>
        {
            foreach (var service in services.Where(s => s.ServiceType == typeof(IHostedService) &&
                         s.ImplementationType != typeof(DatabaseStartupHostedService)).ToArray())
                services.Remove(service);
            services.AddHttpClient(HttpClientNames.GitHub).ConfigurePrimaryHttpMessageHandler(() => github);
        };
        await tester.Start();
        return tester;
    }

    private static async Task StoreLegacyProof(ServerTester tester, string userId)
    {
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.ExecuteAsync("""
            UPDATE "AspNetUsers" SET "GithubGistUrl" = @gistUrl,
                "AccountDetail" = '{"github":"old-login"}'::jsonb WHERE "Id" = @userId
            """, new { userId, gistUrl = GistUrl });
    }

    private static async Task AssertStoredIdentity(ServerTester tester, string userId, long githubUserId, string login, string proof)
    {
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        Assert.Equal(githubUserId, await conn.GetGithubUserId(userId));
        Assert.True(await conn.IsGithubAccountVerified(userId));
        Assert.Equal(login, (await conn.GetAccountDetailSettings(userId))!.Github);
        Assert.Equal(proof, await conn.ExecuteScalarAsync<string>(
            "SELECT \"GithubGistUrl\" FROM \"AspNetUsers\" WHERE \"Id\" = @userId", new { userId }));
    }

    private static async Task<string> Page(HttpClient browser, string path)
    {
        using var response = await browser.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static Task<HttpResponseMessage> Submit(HttpClient browser, string page, string gistUrl, string token) =>
        browser.PostAsync(VerificationPath, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["GistUrl"] = gistUrl,
            ["Token"] = token,
            ["__RequestVerificationToken"] = HttpTestHelpers.AntiforgeryToken(page)
        }));

    private static HttpResponseMessage Proof(string login, long id, string token) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(new JObject
        {
            ["owner"] = new JObject { ["login"] = login, ["id"] = id },
            ["files"] = new JObject { ["proof.txt"] = new JObject { ["content"] = token } }
        }.ToString(), System.Text.Encoding.UTF8, "application/json")
    };

    private sealed class GithubHandler : HttpMessageHandler
    {
        public Func<HttpResponseMessage> Reply { get; set; } = () => new HttpResponseMessage(HttpStatusCode.NotFound);
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!);
            return Task.FromResult(Reply());
        }
    }
}
