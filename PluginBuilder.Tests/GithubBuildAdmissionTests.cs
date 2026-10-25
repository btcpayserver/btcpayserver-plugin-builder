using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PluginBuilder.APIModels;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.DataModels;
using PluginBuilder.HostedServices;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using PluginBuilder.Builds.Services;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class GithubBuildAdmissionTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    private const string Password = "123456";
    private const string Proof = "https://gist.github.com/legacy/abcdef";
    private const string Repository = "https://github.com/legacy/plugin";

    [Theory]
    [InlineData("lock")]
    [InlineData("id")]
    [InlineData("username")]
    public async Task AdmissionRechecksRestrictionsAfterWaitingForTheUser(string restriction)
    {
        await using var tester = CreateMigrationTester("GithubAdmissionRace");
        await tester.RunScriptsUntil("31.GithubBans");
        await tester.RunRemainingScripts();
        await using var blocker = await tester.Open();
        await blocker.ExecuteAsync("""
            INSERT INTO "AspNetUsers" ("Id", "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount", "GithubUserId", "GithubGistUrl", "AccountDetail")
            VALUES ('owner', TRUE, FALSE, FALSE, FALSE, 0, @githubUserId, @Proof, '{"github":"Owner"}'::jsonb);
            """, new { githubUserId = restriction == "username" ? (long?)null : 123, Proof });
        var slug = new PluginSlug("github-admission");
        await blocker.NewPlugin(slug, "owner");
        await using var submitter = await tester.Open();
        await using var tx = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync("""SELECT 1 FROM "AspNetUsers" WHERE "Id" = 'owner' FOR UPDATE""", transaction: tx);
        var pending = submitter.NewBuildWithinUserLimit(slug, new PluginBuildParameters(Repository), "owner", true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await blocker.ExecuteScalarAsync<bool>(new CommandDefinition(
                   "SELECT cardinality(pg_blocking_pids(@pid)) > 0", new { pid = submitter.ProcessID }, tx,
                   cancellationToken: deadline.Token)))
            await Task.Delay(10, deadline.Token);

        await blocker.ExecuteAsync(restriction switch
        {
            "id" => "INSERT INTO github_bans (github_user_id, github_login, reason) VALUES (123, 'another-login', 'incident')",
            "username" => "INSERT INTO github_bans (github_login, reason) VALUES ('OWNER', 'incident')",
            _ => "INSERT INTO admin_account_locks (user_id, locked_until, reason) VALUES ('owner', 'infinity', 'incident')"
        }, transaction: tx);
        await tx.CommitAsync();

        await Assert.ThrowsAsync<BuildServiceException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, await blocker.ExecuteScalarAsync<int>("SELECT count(*) FROM builds"));
        Assert.Equal(0, await blocker.ExecuteScalarAsync<int>("SELECT count(*) FROM builds_ids"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task VerifiedLegacyCanBuildThroughUiAndApiUnlessItsUsernameIsBanned(bool api, bool banned)
    {
        var github = new AdmissionGithub();
        var sandbox = new AdmissionTestSandbox();
        await using var tester = await StartServer(github, sandbox);
        var email = $"legacy-{Guid.NewGuid():N}@example.com";
        var userId = await tester.CreateFakeUserAsync(email, Password, githubVerified: false);
        var slug = new PluginSlug("legacy-admission");
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.NewPlugin(slug, userId);
        await conn.ExecuteAsync("""UPDATE "AspNetUsers" SET "GithubGistUrl" = @Proof, "AccountDetail" = '{"github":"Owner"}'::jsonb WHERE "Id" = @userId""", new { userId, Proof });
        if (banned)
            await conn.ExecuteAsync("INSERT INTO github_bans (github_login, reason) VALUES ('OWNER', 'incident')");
        await conn.SettingsSetAsync(SettingsKeys.VerifiedGithub, "true");
        await conn.SettingsSetAsync(SettingsKeys.NewBuildsEnabled, "true");
        await tester.GetService<AdminSettingsCache>().RefreshAllAdminSettings(conn);
        using var client = HttpTestHelpers.CreateBrowser(tester);
        string token = "";
        if (api)
            client.SetBasicAuth(email, Password);
        else
        {
            await HttpTestHelpers.LogIn(client, email, Password);
            using var proofPage = await client.GetAsync(banned ? "/account/details" : $"/plugins/{slug}/create");
            Assert.Equal(HttpStatusCode.OK, proofPage.StatusCode);
            token = HttpTestHelpers.AntiforgeryToken(await proofPage.Content.ReadAsStringAsync());
        }

        using HttpContent body = api
            ? JsonContent.Create(new { gitRepository = Repository, gitRef = "main" })
            : new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["GitRepository"] = Repository, ["GitRef"] = "main", ["__RequestVerificationToken"] = token
            });
        using var response = await client.PostAsync(api ? $"/api/v1/plugins/{slug}/builds" : $"/plugins/{slug}/create", body);
        Assert.Equal(api ? banned ? HttpStatusCode.Forbidden : HttpStatusCode.Created : HttpStatusCode.Redirect, response.StatusCode);
        if (!api)
            Assert.Equal(banned ? "/account/VerifyGithubAccount" : $"/plugins/{slug}/builds/0", new Uri(client.BaseAddress!, response.Headers.Location!).AbsolutePath);
        Assert.Equal(banned ? Array.Empty<string>() : new[] { userId }, (await conn.QueryAsync<string>("SELECT triggered_by FROM builds")).ToArray());
        if (!banned)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (await conn.ExecuteScalarAsync<string>("SELECT state FROM builds") != "failed")
                await Task.Delay(10, deadline.Token);
            Assert.Single(sandbox.StartedPreparations);
        }
        Assert.Equal(0, github.Requests);
        Assert.Equal(banned, await conn.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM admin_account_locks WHERE user_id = @userId AND locked_until > CURRENT_TIMESTAMP)", new { userId }));
        Assert.Null(await conn.ExecuteScalarAsync<long?>("""SELECT "GithubUserId" FROM "AspNetUsers" WHERE "Id" = @userId""", new { userId }));
        Assert.Equal(Proof, await conn.ExecuteScalarAsync<string>("""SELECT "GithubGistUrl" FROM "AspNetUsers" WHERE "Id" = @userId""", new { userId }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhitelistAndDisabledVerificationDoNotBypassAKnownGithubBan(bool usernameBan)
    {
        var github = new AdmissionGithub();
        await using var tester = await StartServer(github);
        var email = $"banned-{Guid.NewGuid():N}@example.com";
        var userId = await tester.CreateFakeUserAsync(email, Password);
        var slug = new PluginSlug("banned-whitelist");
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.NewPlugin(slug, userId);
        await conn.ExecuteAsync("""
            UPDATE "AspNetUsers" SET "GithubUserId" = @id, "AccountDetail" = '{"github":"Owner"}'::jsonb WHERE "Id" = @userId;
            INSERT INTO github_bans (github_user_id, github_login, reason) VALUES (@id, 'OWNER', 'incident');
            INSERT INTO build_whitelist (user_id) VALUES (@userId);
            """, new { userId, id = usernameBan ? (long?)null : 123 });
        await conn.SettingsSetAsync(SettingsKeys.VerifiedGithub, "false");
        await conn.SettingsSetAsync(SettingsKeys.NewBuildsEnabled, "false");
        await tester.GetService<AdminSettingsCache>().RefreshAllAdminSettings(conn);
        using var client = tester.CreateHttpClient().SetBasicAuth(email, Password);

        using var response = await client.PostAsJsonAsync($"/api/v1/plugins/{slug}/builds", new { gitRepository = Repository, gitRef = "main" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, github.Requests);
        Assert.True(await conn.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM admin_account_locks WHERE user_id = @userId AND locked_until > CURRENT_TIMESTAMP)", new { userId }));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM builds"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecutorRejectsABannedIdentityBeforePreparingTheSandbox(bool usernameBan)
    {
        var github = new AdmissionGithub();
        var sandbox = new AdmissionTestSandbox();
        await using var tester = await StartServer(github, sandbox);
        var userId = await tester.CreateFakeUserAsync();
        var slug = new PluginSlug("banned-executor");
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.NewPlugin(slug, userId);
        var id = await conn.NewBuild(slug, new PluginBuildParameters(Repository), userId);
        await conn.ExecuteAsync("""
            UPDATE "AspNetUsers" SET "GithubUserId" = @id, "AccountDetail" = '{"github":"Owner"}'::jsonb WHERE "Id" = @userId;
            INSERT INTO github_bans (github_user_id, github_login, reason) VALUES (@id, 'OWNER', 'incident');
            """, new { userId, id = usernameBan ? (long?)null : 123 });

        await tester.GetService<BuildService>().Build(new FullBuildId(slug, id));

        Assert.Empty(sandbox.StartedPreparations);
        Assert.Equal("failed", await conn.ExecuteScalarAsync<string>("SELECT state FROM builds WHERE plugin_slug = @slug AND id = @id", new { slug = slug.ToString(), id }));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM versions"));
        Assert.Equal(0, github.Requests);
    }

    private async Task<ServerTester> StartServer(AdmissionGithub github, AdmissionTestSandbox? sandbox = null)
    {
        var tester = Create("GithubBuildAdmission");
        tester.ReuseDatabase = false;
        tester.ConfigureServices = services =>
        {
            sandbox?.Register(services);
            foreach (var service in services.Where(s => s.ServiceType == typeof(IHostedService) &&
                         s.ImplementationType != typeof(DatabaseStartupHostedService)).ToArray())
                services.Remove(service);
            services.RemoveAll<IGitHostingProvider>();
            services.AddSingleton<IGitHostingProvider>(github);
            services.AddHttpClient(HttpClientNames.GitHub).ConfigurePrimaryHttpMessageHandler(() => github);
        };
        await tester.Start();
        tester.GetService<BuildExecutorState>().MarkReady(
            "sha256:1111111111111111111111111111111111111111111111111111111111111111",
            "sha256:2222222222222222222222222222222222222222222222222222222222222222");
        return tester;
    }

    private sealed class AdmissionGithub : HttpMessageHandler, IGitHostingProvider
    {
        public bool CanHandle(string repoUrl) => repoUrl == Repository;
        public Task<string> FetchIdentifierFromCsprojAsync(string repoUrl, string gitRef, string? pluginDir = null) => Task.FromResult("Legacy.TestPlugin");
        public Task<List<GitHubContributor>> GetContributorsAsync(string repoUrl, string pluginDir) => Task.FromResult(new List<GitHubContributor>());
        public string? GetSourceUrl(string repoUrl, string? commit, string? pluginDir) => null;
        public (string Owner, string RepoName)? ParseRepository(string repoUrl) => null;

        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
