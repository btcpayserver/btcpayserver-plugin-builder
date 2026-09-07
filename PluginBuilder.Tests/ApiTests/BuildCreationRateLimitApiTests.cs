using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.DataModels;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.ApiTests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class BuildCreationRateLimitApiTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    private const string Password = "123456";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BasicAuthUsersBehindSameIpHaveSeparateBuildBuckets(bool sharePlugin)
    {
        await using var tester = Create("BuildRateLimitBasicAuth");
        tester.ReuseDatabase = false;
        await tester.Start();

        var firstEmail = $"build-rate-first-{Guid.NewGuid():N}@example.com";
        var secondEmail = $"build-rate-second-{Guid.NewGuid():N}@example.com";
        var firstOwnerId = await tester.CreateFakeUserAsync(firstEmail, Password);
        var secondOwnerId = await tester.CreateFakeUserAsync(secondEmail, Password);
        var firstPlugin = "build-rate-first-" + Guid.NewGuid().ToString("N")[..8];
        var secondPlugin = sharePlugin ? firstPlugin : "build-rate-second-" + Guid.NewGuid().ToString("N")[..8];

        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.NewPlugin(firstPlugin, firstOwnerId);
        if (sharePlugin)
            await conn.AddUserPlugin(firstPlugin, secondOwnerId);
        else
            await conn.NewPlugin(secondPlugin, secondOwnerId);
        await conn.SettingsSetAsync(SettingsKeys.NewBuildsEnabled, "false");
        await tester.GetService<AdminSettingsCache>().RefreshFeatureSettings(conn);

        using var firstClient = tester.CreateHttpClient().SetBasicAuth(firstEmail, Password);
        using var secondClient = tester.CreateHttpClient().SetBasicAuth(secondEmail, Password);

        for (var i = 0; i < 4; i++)
        {
            using var response = await PostBuild(firstClient, firstPlugin);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }

        using (var exhaustedResponse = await PostBuild(firstClient, firstPlugin))
            Assert.Equal(HttpStatusCode.TooManyRequests, exhaustedResponse.StatusCode);

        using var secondUserResponse = await PostBuild(secondClient, secondPlugin);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, secondUserResponse.StatusCode);
    }

    [Fact]
    public async Task OneOwnerSharesBuildBucketAcrossPlugins()
    {
        await using var tester = Create("BuildRateLimitMultiPlugin");
        tester.ReuseDatabase = false;
        await tester.Start();

        var email = $"build-rate-owner-{Guid.NewGuid():N}@example.com";
        var ownerId = await tester.CreateFakeUserAsync(email, Password);
        var firstPlugin = "build-rate-a-" + Guid.NewGuid().ToString("N")[..8];
        var secondPlugin = "build-rate-b-" + Guid.NewGuid().ToString("N")[..8];
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.NewPlugin(firstPlugin, ownerId);
        await conn.NewPlugin(secondPlugin, ownerId);
        await conn.SettingsSetAsync(SettingsKeys.NewBuildsEnabled, "false");
        await tester.GetService<AdminSettingsCache>().RefreshFeatureSettings(conn);

        using var client = tester.CreateHttpClient().SetBasicAuth(email, Password);
        for (var i = 0; i < 4; i++)
        {
            using var response = await PostBuild(client, i % 2 == 0 ? firstPlugin : secondPlugin);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }

        using (var firstExhausted = await PostBuild(client, firstPlugin))
            Assert.Equal(HttpStatusCode.TooManyRequests, firstExhausted.StatusCode);
        using (var secondExhausted = await PostBuild(client, secondPlugin))
            Assert.Equal(HttpStatusCode.TooManyRequests, secondExhausted.StatusCode);
    }

    [Fact]
    public async Task CookieUiAndBasicApiShareTheUsersBuildBucket()
    {
        await using var tester = Create("BuildRateLimitSharedUiApi");
        tester.ReuseDatabase = false;
        await tester.Start();

        var firstEmail = $"build-rate-ui-first-{Guid.NewGuid():N}@example.com";
        var secondEmail = $"build-rate-ui-second-{Guid.NewGuid():N}@example.com";
        var firstOwner = await tester.CreateFakeUserAsync(firstEmail, Password);
        var secondOwner = await tester.CreateFakeUserAsync(secondEmail, Password);
        var firstPlugin = "build-rate-ui-first-" + Guid.NewGuid().ToString("N")[..8];
        var secondPlugin = "build-rate-ui-second-" + Guid.NewGuid().ToString("N")[..8];
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.NewPlugin(firstPlugin, firstOwner);
        await conn.NewPlugin(secondPlugin, secondOwner);

        using var firstBrowser = CreateBrowser(tester);
        using var secondBrowser = CreateBrowser(tester);
        await LogIn(firstBrowser, firstEmail);
        await LogIn(secondBrowser, secondEmail);
        var firstToken = await BuildFormToken(firstBrowser, firstPlugin);
        var secondToken = await BuildFormToken(secondBrowser, secondPlugin);
        using var firstApi = tester.CreateHttpClient().SetBasicAuth(firstEmail, Password);
        using var secondApi = tester.CreateHttpClient().SetBasicAuth(secondEmail, Password);

        // Valid cookie sessions and antiforgery tokens reach the real controller.
        // Disable execution so these admission checks never start a build or fetch a repository.
        await conn.SettingsSetAsync(SettingsKeys.NewBuildsEnabled, "false");
        await tester.GetService<AdminSettingsCache>().RefreshFeatureSettings(conn);

        for (var i = 0; i < 4; i++)
        {
            using var response = i % 2 == 0
                ? await PostUiBuild(firstBrowser, firstPlugin, firstToken)
                : await PostBuild(firstApi, firstPlugin);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }

        using (var exhaustedUi = await PostUiBuild(firstBrowser, firstPlugin, firstToken))
            Assert.Equal(HttpStatusCode.TooManyRequests, exhaustedUi.StatusCode);
        using (var exhaustedApi = await PostBuild(firstApi, firstPlugin))
            Assert.Equal(HttpStatusCode.TooManyRequests, exhaustedApi.StatusCode);

        using (var otherUi = await PostUiBuild(secondBrowser, secondPlugin, secondToken))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, otherUi.StatusCode);
        using (var otherApi = await PostBuild(secondApi, secondPlugin))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, otherApi.StatusCode);
    }

    private static HttpClient CreateBrowser(ServerTester tester) => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        CookieContainer = new CookieContainer()
    })
    {
        BaseAddress = new Uri(tester.WebApp.Urls.First())
    };

    private static async Task LogIn(HttpClient browser, string email)
    {
        using var page = await browser.GetAsync("/login");
        page.EnsureSuccessStatusCode();
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = Password,
            ["__RequestVerificationToken"] = ExtractAntiforgeryToken(await page.Content.ReadAsStringAsync())
        });
        using var response = await browser.PostAsync("/login", form);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task<string> BuildFormToken(HttpClient browser, string pluginSlug)
    {
        using var page = await browser.GetAsync($"/plugins/{pluginSlug}/create");
        page.EnsureSuccessStatusCode();
        return ExtractAntiforgeryToken(await page.Content.ReadAsStringAsync());
    }

    private static async Task<HttpResponseMessage> PostUiBuild(HttpClient browser, string pluginSlug, string token)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["GitRepository"] = "https://github.com/example/plugin",
            ["GitRef"] = "main",
            ["BuildConfig"] = "Release",
            ["__RequestVerificationToken"] = token
        });
        return await browser.PostAsync($"/plugins/{pluginSlug}/create", form);
    }

    private static string ExtractAntiforgeryToken(string html)
    {
        var input = Regex.Match(html,
            "<input\\b(?=[^>]*\\bname=\"__RequestVerificationToken\")[^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        Assert.True(input.Success, "HTML did not contain an antiforgery token input.");
        var value = Regex.Match(input.Value, "\\bvalue=\"([^\"]+)\"", RegexOptions.CultureInvariant);
        Assert.True(value.Success, "Antiforgery token input did not contain a value.");
        return WebUtility.HtmlDecode(value.Groups[1].Value);
    }

    private static async Task<HttpResponseMessage> PostBuild(HttpClient client, string pluginSlug)
    {
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        return await client.PostAsync($"/api/v1/plugins/{pluginSlug}/builds", content);
    }
}
