using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json.Linq;
using Npgsql;
using PluginBuilder.APIModels;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.DataModels;
using PluginBuilder.HostedServices;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

using PluginBuilder.Builds;
using PluginBuilder.Builds.Services;

namespace PluginBuilder.Tests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class BuildUserLimitTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    private const string Password = "123456";
    private const int Requests = BuildPolicy.MaxActiveBuildsPerUser + 2;

    [Fact]
    public async Task ConcurrentRequests_AdmitOnlyTheUsersLimitUntilABuildFinishes()
    {
        TaskCompletionSource releasePreparation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var sandbox = new AdmissionTestSandbox { PreparationBlockedUntil = releasePreparation.Task };
        // Hold every request after validation so all of them race for admission together.
        var git = new GatedGitProvider(Requests);
        await using var tester = Create("BuildUserLimit");
        tester.ReuseDatabase = false;
        tester.ConfigureServices = services =>
        {
            sandbox.Register(services);
            services.RemoveAll<IGitHostingProvider>();
            services.AddSingleton<IGitHostingProvider>(git);
        };
        await tester.Start();
        MarkExecutorReady(tester);
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await EnableBuilds(tester, conn);

        var ownerEmail = NewEmail();
        var coOwnerEmail = NewEmail();
        var ownerId = await tester.CreateFakeUserAsync(ownerEmail);
        var coOwnerId = await tester.CreateFakeUserAsync(coOwnerEmail);
        var slug = NewSlug();
        await conn.NewPlugin(slug, ownerId);
        await conn.AddUserPlugin(slug, coOwnerId);
        // Claim the identifier up front; this test is about admission, not the first-build identifier claim.
        await conn.ExecuteAsync("UPDATE plugins SET identifier = @identifier WHERE slug = @slug",
            new { identifier = GatedGitProvider.Identifier, slug = slug.ToString() });
        using var owner = CreateClient(tester).SetBasicAuth(ownerEmail, Password);
        using var coOwner = CreateClient(tester).SetBasicAuth(coOwnerEmail, Password);

        try
        {
            var responses = await Task.WhenAll(Enumerable.Range(0, Requests).Select(_ => PostApiBuild(owner, git, slug)));
            Assert.Equal(BuildPolicy.MaxActiveBuildsPerUser, responses.Count(r => r.Status == HttpStatusCode.Created));
            Assert.All(responses.Where(r => r.Status != HttpStatusCode.Created), r =>
            {
                Assert.Equal(HttpStatusCode.TooManyRequests, r.Status);
                Assert.Equal("too-many-active-builds", JObject.Parse(r.Body).Value<string>("error"));
            });
            Assert.Equal(BuildPolicy.MaxActiveBuildsPerUser, await BuildCount(conn, slug));

            // The limit belongs to the user, not to the plugin.
            Assert.Equal(HttpStatusCode.Created, (await PostApiBuild(coOwner, git, slug)).Status);

            releasePreparation.TrySetResult();
            await WaitForFinishedBuilds(conn, slug);
            Assert.Equal(HttpStatusCode.Created, (await PostApiBuild(owner, git, slug)).Status);
            await WaitForFinishedBuilds(conn, slug);
            Assert.Equal(BuildPolicy.MaxActiveBuildsPerUser + 2, await BuildCount(conn, slug));
        }
        finally
        {
            releasePreparation.TrySetResult();
        }
    }

    [Fact]
    public async Task Ui_ExplainsTheLimitWithoutCreatingABuild()
    {
        var git = new GatedGitProvider(1);
        await using var tester = Create("BuildUserLimitUi");
        tester.ReuseDatabase = false;
        tester.ConfigureServices = services =>
        {
            services.RemoveAll<IGitHostingProvider>();
            services.AddSingleton<IGitHostingProvider>(git);
        };
        await tester.Start();
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await EnableBuilds(tester, conn);

        var email = NewEmail();
        var userId = await tester.CreateFakeUserAsync(email);
        var slug = NewSlug();
        await conn.NewPlugin(slug, userId);
        for (var i = 0; i < BuildPolicy.MaxActiveBuildsPerUser; i++)
            await conn.NewBuild(slug, new PluginBuildParameters(git.RepositoryUrl), triggeredBy: userId);

        using var browser = CreateClient(tester);
        await LogIn(browser, email);
        using var page = await browser.GetAsync($"/plugins/{slug}/create");
        page.EnsureSuccessStatusCode();
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["GitRepository"] = git.RepositoryUrl,
            ["GitRef"] = "main",
            ["BuildConfig"] = ServerTester.BuildCfg,
            ["__RequestVerificationToken"] = AntiforgeryToken(await page.Content.ReadAsStringAsync())
        });
        using var response = await browser.PostAsync($"/plugins/{slug}/create", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"You already have {BuildPolicy.MaxActiveBuildsPerUser} builds in progress",
            await response.Content.ReadAsStringAsync());
        Assert.Equal(BuildPolicy.MaxActiveBuildsPerUser, await BuildCount(conn, slug));
    }

    [Fact]
    public async Task Restart_FailsEveryUnfinishedBuild()
    {
        await using var tester = Create("BuildUserLimitRestart");
        tester.ReuseDatabase = false;
        await tester.Start();
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        var userId = await tester.CreateFakeUserAsync();
        var slug = NewSlug();
        await conn.NewPlugin(slug, userId);
        var states = Enum.GetValues<BuildStates>();
        foreach (var state in states)
        {
            var buildId = await conn.NewBuild(slug, new PluginBuildParameters("https://github.com/example/repository"), userId);
            await conn.UpdateBuild(new FullBuildId(slug, buildId), state, null);
        }

        var startup = tester.WebApp.Services.GetServices<IHostedService>().OfType<DatabaseStartupHostedService>().Single();
        await startup.StartAsync(CancellationToken.None);

        var persisted = (await conn.QueryAsync<string>(
            "SELECT state FROM builds WHERE plugin_slug = @slug ORDER BY id", new { slug = slug.ToString() })).ToArray();
        Assert.Equal(states.Select(s => (s.IsTerminal() ? s : BuildStates.Failed).ToEventName()), persisted);
    }

    private static async Task<(HttpStatusCode Status, string Body)> PostApiBuild(HttpClient client, GatedGitProvider git, PluginSlug slug)
    {
        using var content = new StringContent(new JObject
        {
            ["gitRepository"] = git.RepositoryUrl,
            ["gitRef"] = "main",
            ["buildConfig"] = ServerTester.BuildCfg
        }.ToString(), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync($"/api/v1/plugins/{slug}/builds", content);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task EnableBuilds(ServerTester tester, NpgsqlConnection conn)
    {
        await conn.SettingsSetAsync(SettingsKeys.NewBuildsEnabled, "true");
        await tester.GetService<AdminSettingsCache>().RefreshFeatureSettings(conn);
    }

    private static void MarkExecutorReady(ServerTester tester) => tester.GetService<BuildExecutorState>().MarkReady(
        "sha256:1111111111111111111111111111111111111111111111111111111111111111",
        "sha256:2222222222222222222222222222222222222222222222222222222222222222");

    private static string NewEmail() => $"user-limit-{Guid.NewGuid():N}@example.com";
    private static PluginSlug NewSlug() => new("user-limit-" + Guid.NewGuid().ToString("N")[..8]);

    private static Task<int> BuildCount(NpgsqlConnection conn, PluginSlug slug) => conn.ExecuteScalarAsync<int>(
        "SELECT COUNT(*) FROM builds WHERE plugin_slug = @slug", new { slug = slug.ToString() });

    private static async Task WaitForFinishedBuilds(NpgsqlConnection conn, PluginSlug slug)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await conn.ExecuteScalarAsync<bool>(
                   "SELECT EXISTS(SELECT 1 FROM builds WHERE plugin_slug = @slug AND state = ANY(@states))",
                   new { slug = slug.ToString(), states = BuildStatesExtensions.UnfinishedEventNames }))
            await Task.Delay(10, timeout.Token);
    }

    private static HttpClient CreateClient(ServerTester tester) => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        CookieContainer = new CookieContainer()
    }) { BaseAddress = new Uri(tester.WebApp.Urls.First()) };

    private static async Task LogIn(HttpClient client, string email)
    {
        using var page = await client.GetAsync("/login");
        page.EnsureSuccessStatusCode();
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = Password,
            ["__RequestVerificationToken"] = AntiforgeryToken(await page.Content.ReadAsStringAsync())
        });
        using var response = await client.PostAsync("/login", form);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static string AntiforgeryToken(string html)
    {
        var input = Regex.Match(html, "<input\\b(?=[^>]*\\bname=\"__RequestVerificationToken\")[^>]*\\bvalue=\"([^\"]+)\"",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        Assert.True(input.Success, "Page must contain an antiforgery token.");
        return WebUtility.HtmlDecode(input.Groups[1].Value);
    }

    private sealed class GatedGitProvider(int requests) : IGitHostingProvider
    {
        private readonly TaskCompletionSource _allEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _entered;
        public const string Identifier = "UserLimit.TestPlugin";
        public string RepositoryUrl { get; } = $"https://github.com/user-limit-{Guid.NewGuid():N}/repository";
        public bool CanHandle(string repoUrl) => repoUrl == RepositoryUrl;
        public async Task<string> FetchIdentifierFromCsprojAsync(string repoUrl, string gitRef, string? pluginDir = null)
        {
            if (Interlocked.Increment(ref _entered) >= requests)
                _allEntered.TrySetResult();
            await _allEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return Identifier;
        }
        public Task<List<GitHubContributor>> GetContributorsAsync(string repoUrl, string pluginDir) => Task.FromResult(new List<GitHubContributor>());
        public string? GetSourceUrl(string repoUrl, string? commit, string? pluginDir) => null;
        public (string Owner, string RepoName)? ParseRepository(string repoUrl) => null;
    }
}
