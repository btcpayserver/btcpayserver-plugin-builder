using System.Net;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json.Linq;
using Npgsql;
using PluginBuilder.DataModels;
using PluginBuilder.HostedServices;
using PluginBuilder.Services;
using PluginBuilder.Util;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class ListingReviewTargetTests(ITestOutputHelper output) : UnitTestBase(output)
{
    [Theory]
    [InlineData(null, null, "https://github.com/example/plugin.git/")]
    [InlineData("src/Plugin", "src/Plugin", "https://github.com/example/plugin.git/")]
    [InlineData("src/Plugin #1?", "src/Plugin%20%231%3F", "https://github.com/example/plugin.git/")]
    [InlineData(null, null, "https://github.com/Example/Plugin.git/")]
    [InlineData("src/Plugin", "src/Plugin", "https://github.com:443/example/plugin.git/")]
    [InlineData("src/Plugin #1?", "src/Plugin%20%231%3F", "https://www.github.com:443/Example/Plugin.git/")]
    public async Task OwnerSubmissionPinsBuildSourceInRequestEventAndAdminReads(string? pluginDirectory, string? sourcePath, string currentRepository)
    {
        await using var server = await StartServer();
        await using var conn = await server.GetService<DBConnectionFactory>().Open();
        var owner = await server.CreateFakeUserAsync("listing-owner@example.com");
        var slug = new PluginSlug("listing-source");
        await conn.NewPlugin(slug, owner);
        await conn.SetPluginSettings(slug, new PluginSettings
        {
            PluginTitle = "Example", Description = "Example", GitRepository = "https://github.com/example/settings-repo",
            Documentation = "https://example.com/docs", VideoUrl = "https://youtu.be/abc", Logo = "https://example.com/logo.png"
        });
        await conn.ExecuteAsync("""
            UPDATE "AspNetUsers" SET "AccountDetail" = '{"nostr":{"npub":"npub1example"}}'::jsonb WHERE "Id" = @owner
            """, new { owner });
        using var browser = HttpTestHelpers.CreateBrowser(server);
        await HttpTestHelpers.LogIn(browser, "listing-owner@example.com", "123456");
        var path = $"/plugins/{slug}/request-listing";
        var page = await browser.GetStringAsync(path);
        Assert.Contains("A stable release", page);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["ReleaseNote"] = "Release", ["UserReviews"] = "Reviewed", ["TelegramVerificationMessage"] = "https://t.me/btcpayserver/123",
            ["HasReviewTarget"] = "true", ["ReviewTarget.BuildId"] = "999",
            ["ReviewTarget.Repository"] = "https://github.com/forged/repo", ["ReviewTarget.GitCommit"] = new string('f', 40),
            ["ReviewTarget.PluginDirectory"] = "src/Forged",
            ["ReviewTarget.Version"] = "99.0.0", ["ReviewTarget.PreRelease"] = "true",
            ["__RequestVerificationToken"] = HttpTestHelpers.AntiforgeryToken(page)
        });
        using var missing = await browser.PostAsync(path, form);
        Assert.Equal(HttpStatusCode.OK, missing.StatusCode);
        Assert.Contains("A stable release", await missing.Content.ReadAsStringAsync());
        Assert.False(await conn.HasPendingListingRequest(slug));

        var build = await AddVersionBuild(conn, slug, 'a', pluginDirectory, preRelease: true);
        Assert.Equal(0, build.BuildId);
        using var prerelease = await browser.PostAsync(path, form);
        Assert.Equal(HttpStatusCode.OK, prerelease.StatusCode);
        Assert.Contains("A stable release", await prerelease.Content.ReadAsStringAsync());
        Assert.False(await conn.HasPendingListingRequest(slug));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_events WHERE type = 'listing.requested'"));
        Assert.True((await server.GetService<VersionLifecycleService>().ReleaseAsync(slug, PluginVersion.Parse("1.0.0"), owner, null)).Success);
        var failed = new FullBuildId(slug, await conn.NewBuild(slug, new PluginBuildParameters("https://github.com/example/failed-repo")));
        await conn.UpdateBuild(failed, BuildStates.Failed, null);
        using var accepted = await browser.PostAsync(path, form);
        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        var request = (await conn.GetPendingListingRequestForPlugin(slug))!;
        var target = request.ReviewTarget!;
        Assert.Equal(build.BuildId, target.BuildId);
        Assert.Equal("1.0.0", target.Version);
        Assert.Equal(false, target.PreRelease);
        Assert.Equal("https://github.com/example/plugin", target.Repository);
        Assert.Equal(new string('a', 40), target.GitCommit);
        Assert.Equal(pluginDirectory, target.PluginDirectory);
        Assert.Equal(PluginListingRequestStatus.Pending, request.Status);

        await AddVersionBuild(conn, slug, 'b', "src/Other");
        await conn.ExecuteAsync("UPDATE plugins SET settings = settings || '{\"gitRepository\":\"https://github.com/example/changed\"}'::jsonb WHERE slug = @slug",
            new { slug = slug.ToString() });
        Assert.Equal(new string('b', 40), (await conn.GetLatestListingReviewTarget(slug))!.GitCommit);
        var savedTarget = (await conn.GetListingRequest(request.Id))!.ReviewTarget!;
        Assert.Equal(target.ToJson(), savedTarget.ToJson());
        Assert.Equal(pluginDirectory, savedTarget.PluginDirectory);
        Assert.Equal(target.ToJson(), (await conn.GetAllListingRequestsForPlugin(slug)).Single().ReviewTarget!.ToJson());
        var data = JObject.Parse(await conn.QuerySingleAsync<string>("SELECT data::text FROM admin_events WHERE type = 'listing.requested'"));
        Assert.Equal(request.Id, data["listingRequestId"]!.Value<int>());
        Assert.Equal(target.BuildId, data["buildId"]!.Value<long>());
        Assert.Equal("1.0.0", data["version"]!.Value<string>());
        Assert.False(data["preRelease"]!.Value<bool>());
        Assert.Equal(target.Repository, data["repository"]!.Value<string>());
        Assert.Equal(target.GitCommit, data["gitCommit"]!.Value<string>());
        Assert.Equal(pluginDirectory, data["pluginDirectory"]!.Value<string>());

        await CreateAdmin(server, conn);
        using var api = server.CreateHttpClient();
        api.SetBasicAuth("listing-admin@example.com", "123456");
        var detail = JObject.Parse(await api.GetStringAsync($"/api/v1/admin/listing-requests/{request.Id}"));
        var list = JObject.Parse(await api.GetStringAsync("/api/v1/admin/listing-requests"));
        Assert.True(JToken.DeepEquals(JObject.Parse(target.ToJson()), detail["reviewTarget"]));
        Assert.True(JToken.DeepEquals(detail["reviewTarget"], list["items"]![0]!["reviewTarget"]));
        Assert.Equal(pluginDirectory, detail["reviewTarget"]!["pluginDirectory"]!.Value<string>());
        Assert.Equal(pluginDirectory, list["items"]![0]!["reviewTarget"]!["pluginDirectory"]!.Value<string>());
        Assert.Equal("1.0.0", detail["reviewTarget"]!["version"]!.Value<string>());
        Assert.False(detail["reviewTarget"]!["preRelease"]!.Value<bool>());
        Assert.Equal("1.0.0", list["items"]![0]!["reviewTarget"]!["version"]!.Value<string>());
        Assert.False(list["items"]![0]!["reviewTarget"]!["preRelease"]!.Value<bool>());
        using var adminBrowser = HttpTestHelpers.CreateBrowser(server);
        await HttpTestHelpers.LogIn(adminBrowser, "listing-admin@example.com", "123456");
        var adminPage = await adminBrowser.GetStringAsync($"/admin/listing-requests/{request.Id}");
        Assert.Contains(target.GitCommit, adminPage);
        Assert.Contains(target.Repository, adminPage);
        Assert.Contains("Version 1.0.0", adminPage);
        Assert.Contains("Source to review", adminPage);
        Assert.DoesNotContain("Technical details", adminPage);
        var pinnedSourceUrl = $"https://github.com/example/plugin/tree/{new string('a', 40)}" +
                              (sourcePath is null ? "" : $"/{sourcePath}");
        Assert.Contains($"href=\"{pinnedSourceUrl}\"", adminPage);
        Assert.DoesNotContain($"/tree/{new string('b', 40)}", adminPage);
        Assert.DoesNotContain("Repository (current settings)", adminPage);
        Assert.Contains("href=\"https://github.com/example/changed\"", adminPage);
        Assert.Contains("Current settings:", adminPage);

        await conn.SetPluginSettings(slug, new PluginSettings { GitRepository = currentRepository });
        var equivalentRepositoryPage = await adminBrowser.GetStringAsync($"/admin/listing-requests/{request.Id}");
        Assert.Contains($"href=\"{pinnedSourceUrl}\"", equivalentRepositoryPage);
        Assert.DoesNotContain($"href=\"{currentRepository}\"", equivalentRepositoryPage);
        Assert.DoesNotContain("Current settings:", equivalentRepositoryPage);
    }

    [Theory]
    [InlineData(null, null, "https://gitlab.com/example/group/plugin.git/")]
    [InlineData("src/Plugin", "src/Plugin", "https://gitlab.com/example/group/plugin.git/")]
    [InlineData("src/Plugin #1?", "src/Plugin%20%231%3F", "https://gitlab.com/example/group/plugin.git/")]
    [InlineData(null, null, "https://www.gitlab.com/example/group/plugin")]
    [InlineData("src/Plugin", "src/Plugin", "https://www.gitlab.com/example/group/plugin.git/")]
    [InlineData("src/Plugin #1?", "src/Plugin%20%231%3F", "https://www.gitlab.com/example/group/plugin")]
    [InlineData(null, null, "https://gitlab.com:443/example/group/plugin")]
    [InlineData("src/Plugin", "src/Plugin", "https://www.gitlab.com:443/example/group/plugin.git/")]
    [InlineData("src/Plugin #1?", "src/Plugin%20%231%3F", "https://www.gitlab.com:443/example/group/plugin")]
    public async Task AdminReviewTargetLinksToGitLabCommitWithoutDependingOnCurrentSettings(string? pluginDirectory, string? sourcePath, string currentRepository)
    {
        await using var server = await StartServer();
        await using var conn = await server.GetService<DBConnectionFactory>().Open();
        var admin = await CreateAdmin(server, conn);
        var slug = new PluginSlug("listing-gitlab-link");
        await conn.NewPlugin(slug, admin);
        var target = new ListingReviewTarget
        {
            BuildId = 0, Version = "1.0.0", PreRelease = false,
            Repository = "https://gitlab.com/example/group/plugin", GitCommit = new string('a', 40),
            PluginDirectory = pluginDirectory
        };
        var requestId = await conn.CreateListingRequest(slug, "Release", "Proof", "Reviews", null, admin, target);
        using var browser = HttpTestHelpers.CreateBrowser(server);
        await HttpTestHelpers.LogIn(browser, "listing-admin@example.com", "123456");
        var path = $"/admin/listing-requests/{requestId}";
        var sourceUrl = $"https://gitlab.com/example/group/plugin/-/tree/{new string('a', 40)}" +
                        (sourcePath is null ? "" : $"/{sourcePath}");
        var page = await browser.GetStringAsync(path);
        Assert.Contains($"href=\"{sourceUrl}\"", page);
        Assert.Contains("Source to review", page);
        Assert.DoesNotContain("Technical details", page);
        Assert.Contains("Not configured", page);
        Assert.Contains("Current settings:", page);

        await conn.SetPluginSettings(slug, new PluginSettings { GitRepository = currentRepository });
        var equivalentRepositoryPage = await browser.GetStringAsync(path);
        Assert.Contains($"href=\"{sourceUrl}\"", equivalentRepositoryPage);
        Assert.DoesNotContain("Repository (current settings)", equivalentRepositoryPage);
        Assert.DoesNotContain($"href=\"{currentRepository}\"", equivalentRepositoryPage);
        Assert.DoesNotContain("Current settings:", equivalentRepositoryPage);

        const string changedRepository = "https://www.gitlab.com/example/group/changed";
        await conn.SetPluginSettings(slug, new PluginSettings { GitRepository = changedRepository });
        var changedRepositoryPage = await browser.GetStringAsync(path);
        Assert.Contains($"href=\"{sourceUrl}\"", changedRepositoryPage);
        Assert.Contains($"href=\"{changedRepository}\"", changedRepositoryPage);
        Assert.Contains("Current settings:", changedRepositoryPage);
    }

    [Theory]
    [InlineData("gitCommit", "main")]
    [InlineData("gitRepository", "http://github.com/example/plugin")]
    [InlineData("pluginDir", "../Other")]
    public async Task InvalidLatestStableVersionDoesNotFallBackToOlderSource(string field, string value)
    {
        await using var migration = CreateMigrationTester("ListingTargetSelection");
        await migration.RunScriptsUntil("34.ListingReviewTarget");
        await migration.RunRemainingScripts();
        await using var conn = await migration.Open();
        var slug = new PluginSlug("listing-invalid-source");
        await conn.ExecuteAsync("INSERT INTO plugins(slug) VALUES (@slug)", new { slug = slug.ToString() });
        await AddVersionBuild(conn, slug, 'a');
        var latest = await AddVersionBuild(conn, slug, 'b');
        await conn.UpdateBuild(latest, BuildStates.Uploaded, new JObject { [field] = value });
        Assert.Null(await conn.GetLatestListingReviewTarget(slug));
    }

    [Fact]
    public async Task RebuildOfReleasedVersionKeepsTheRegisteredBuildAsReviewTarget()
    {
        await using var migration = CreateMigrationTester("ListingRebuild");
        await migration.RunScriptsUntil("34.ListingReviewTarget");
        await migration.RunRemainingScripts();
        await using var conn = await migration.Open();
        var slug = new PluginSlug("listing-rebuild");
        await conn.ExecuteAsync("INSERT INTO plugins(slug) VALUES (@slug)", new { slug = slug.ToString() });
        var released = await AddVersionBuild(conn, slug, 'a', version: "1.0.0");
        var (rebuilt, manifest) = await AddUploadedBuild(conn, slug, 'b', version: "1.0.0");
        Assert.True(await conn.EnsureIdentifierOwnership(slug, manifest.Identifier));
        Assert.False(await conn.SetVersionBuild(rebuilt, manifest.Version, manifest.BTCPayMinVersion, manifest.BTCPayMaxVersion, true));
        Assert.Equal(released.BuildId, await conn.ExecuteScalarAsync<long>("SELECT build_id FROM versions WHERE plugin_slug = @slug", new { slug = slug.ToString() }));

        var target = (await conn.GetLatestListingReviewTarget(slug))!;
        Assert.Equal(released.BuildId, target.BuildId);
        Assert.NotEqual(rebuilt.BuildId, target.BuildId);
        Assert.Equal(new string('a', 40), target.GitCommit);
        Assert.Equal("1.0.0", target.Version);
        Assert.Equal(false, target.PreRelease);
        var requestId = await conn.CreateListingRequest(slug, "Release", "Proof", "Reviews", null, reviewTarget: target);
        Assert.Equal(released.BuildId, (await conn.GetListingRequest(requestId))!.ReviewTarget!.BuildId);
        Assert.Equal(new string('a', 40), await conn.ExecuteScalarAsync<string>("SELECT data->>'gitCommit' FROM admin_events WHERE type = 'listing.requested'"));
    }

    [Fact]
    public async Task UploadedBuildWithForeignIdentifierAndNoVersionIsNotAReviewTarget()
    {
        await using var migration = CreateMigrationTester("ListingIdentifier");
        await migration.RunScriptsUntil("34.ListingReviewTarget");
        await migration.RunRemainingScripts();
        await using var conn = await migration.Open();
        await conn.ExecuteAsync("INSERT INTO plugins(slug) VALUES ('listing-orphan'), ('identifier-owner')");
        Assert.True(await conn.EnsureIdentifierOwnership("identifier-owner", "Foreign.Plugin"));
        var (build, manifest) = await AddUploadedBuild(conn, "listing-orphan", 'a', identifier: "Foreign.Plugin");
        Assert.False(await conn.EnsureIdentifierOwnership("listing-orphan", manifest.Identifier));
        Assert.Equal("uploaded", await conn.ExecuteScalarAsync<string>("SELECT state FROM builds WHERE plugin_slug = 'listing-orphan' AND id = @id", new { id = build.BuildId }));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM versions WHERE plugin_slug = 'listing-orphan'"));
        Assert.Null(await conn.GetLatestListingReviewTarget("listing-orphan"));
    }

    [Fact]
    public async Task LatestStableVersionWinsOverAHigherBuildIdOfAnOlderVersion()
    {
        await using var migration = CreateMigrationTester("ListingVersionOrder");
        await migration.RunScriptsUntil("34.ListingReviewTarget");
        await migration.RunRemainingScripts();
        await using var conn = await migration.Open();
        var slug = new PluginSlug("listing-version-order");
        await conn.ExecuteAsync("INSERT INTO plugins(slug) VALUES (@slug)", new { slug = slug.ToString() });
        var latestVersion = await AddVersionBuild(conn, slug, 'a', version: "2.0.0");
        var laterBuild = await AddVersionBuild(conn, slug, 'b', version: "1.0.0");
        Assert.True(laterBuild.BuildId > latestVersion.BuildId);

        var target = (await conn.GetLatestListingReviewTarget(slug))!;
        Assert.Equal(latestVersion.BuildId, target.BuildId);
        Assert.Equal("2.0.0", target.Version);
        Assert.Equal(new string('a', 40), target.GitCommit);
        Assert.Equal(false, target.PreRelease);
    }

    [Fact]
    public async Task NewerPrereleaseDoesNotReplaceTheLatestStableReviewTarget()
    {
        await using var migration = CreateMigrationTester("ListingPrerelease");
        await migration.RunScriptsUntil("34.ListingReviewTarget");
        await migration.RunRemainingScripts();
        await using var conn = await migration.Open();
        var slug = new PluginSlug("listing-prerelease");
        await conn.ExecuteAsync("INSERT INTO plugins(slug) VALUES (@slug)", new { slug = slug.ToString() });
        var stable = await AddVersionBuild(conn, slug, 'a', version: "1.0.0");
        await AddVersionBuild(conn, slug, 'b', version: "2.0.0", preRelease: true);

        var target = (await conn.GetLatestListingReviewTarget(slug))!;
        Assert.Equal(stable.BuildId, target.BuildId);
        Assert.Equal("1.0.0", target.Version);
        Assert.Equal(false, target.PreRelease);
    }

    [Fact]
    public void OldSnapshotsDoNotInventVersionOrPrereleaseMetadata()
    {
        var target = ListingReviewTarget.FromJson("""
            {"buildId":0,"repository":"https://github.com/example/plugin","gitCommit":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","pluginDirectory":null}
            """)!;
        Assert.Null(target.Version);
        Assert.Null(target.PreRelease);
    }

    [Fact]
    public async Task MigrationPreservesLegacyDecisionsWithoutInferringHistoricalSources()
    {
        await using var migration = CreateMigrationTester("ListingTargetMigration");
        await migration.RunScriptsUntil("34.ListingReviewTarget");
        await using var conn = await migration.Open();
        await conn.ExecuteAsync("""
            INSERT INTO plugins(slug) VALUES ('listing-legacy');
            INSERT INTO builds(plugin_slug, id, state, build_info)
            VALUES ('listing-legacy', 0, 'uploaded', @source::jsonb);
            INSERT INTO versions(plugin_slug, ver, build_id, btcpay_min_ver, pre_release)
            VALUES ('listing-legacy', ARRAY[1,0,0], 0, ARRAY[0,0,0,0], FALSE);
            INSERT INTO plugin_listing_requests(plugin_slug, release_note, telegram_verification_message, user_reviews, status)
            VALUES ('listing-legacy', 'Release', 'Proof', 'Reviews', 'pending'),
                   ('listing-legacy', 'Release', 'Proof', 'Reviews', 'approved'),
                   ('listing-legacy', 'Release', 'Proof', 'Reviews', 'rejected');
            """, new { source = new JObject { ["gitRepository"] = "https://github.com/example/plugin", ["gitCommit"] = new string('a', 40) }.ToString() });
        var before = await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_events");
        await migration.RunRemainingScripts();
        Assert.Equal(before, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_events"));
        var requests = await conn.GetAllListingRequestsForPlugin("listing-legacy");
        Assert.Equal(new[] { PluginListingRequestStatus.Pending, PluginListingRequestStatus.Approved, PluginListingRequestStatus.Rejected },
            requests.OrderBy(r => r.Id).Select(r => r.Status));
        Assert.All(requests, r => Assert.Null(r.ReviewTarget));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_events WHERE type = 'listing.requested' AND data ? 'gitCommit'"));

        var currentTarget = await conn.GetLatestListingReviewTarget("listing-legacy");
        Assert.NotNull(currentTarget);
        await using (var transaction = await conn.BeginTransactionAsync())
        {
            await conn.CreateListingRequest("listing-legacy", "Release", "Proof", "Reviews", null,
                reviewTarget: currentTarget);
            await transaction.RollbackAsync();
        }
        Assert.Equal(before, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_events"));
        Assert.Equal(3, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM plugin_listing_requests"));
    }

    [Fact]
    public async Task LegacyListingRemainsReviewableWithoutTargetOrApprovalNote()
    {
        await using var server = await StartServer();
        await using var conn = await server.GetService<DBConnectionFactory>().Open();
        var admin = await CreateAdmin(server, conn);
        var slug = new PluginSlug("listing-legacy-review");
        await conn.NewPlugin(slug, admin);
        await conn.SetPluginSettings(slug, new PluginSettings { GitRepository = "https://github.com/example/current" });
        var requestId = await conn.CreateListingRequest(slug, "Release", "Proof", "Reviews", null, admin);
        await AddVersionBuild(conn, slug, 'a');
        using var api = server.CreateHttpClient();
        api.SetBasicAuth("listing-admin@example.com", "123456");
        var detail = JObject.Parse(await api.GetStringAsync($"/api/v1/admin/listing-requests/{requestId}"));
        Assert.Equal(JTokenType.Null, detail["reviewTarget"]!.Type);
        using var browser = HttpTestHelpers.CreateBrowser(server);
        await HttpTestHelpers.LogIn(browser, "listing-admin@example.com", "123456");
        var page = await browser.GetStringAsync($"/admin/listing-requests/{requestId}");
        Assert.Contains("Repository (current settings)", page);
        Assert.Contains("href=\"https://github.com/example/current\"", page);
        Assert.DoesNotContain("Current settings:", page);
        using var approved = await browser.PostAsync($"/admin/listing-requests/{requestId}/approve", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = HttpTestHelpers.AntiforgeryToken(page)
        }));
        Assert.Equal(HttpStatusCode.Redirect, approved.StatusCode);
        var request = (await conn.GetListingRequest(requestId))!;
        Assert.Equal(PluginListingRequestStatus.Approved, request.Status);
        Assert.Null(request.ReviewTarget);
        Assert.Equal("listed", await conn.ExecuteScalarAsync<string>("SELECT visibility::text FROM plugins WHERE slug = @slug", new { slug = slug.ToString() }));
    }

    private async Task<ServerTester> StartServer()
    {
        var server = Create("ListingReviewTarget");
        server.ReuseDatabase = false;
        server.ConfigureServices = services =>
        {
            foreach (var descriptor in services.Where(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType != typeof(DatabaseStartupHostedService)).ToArray())
                services.Remove(descriptor);
        };
        await server.Start();
        return server;
    }

    private static async Task<string> CreateAdmin(ServerTester server, NpgsqlConnection conn)
    {
        var id = await server.CreateFakeUserAsync("listing-admin@example.com");
        await conn.ExecuteAsync("""INSERT INTO "AspNetUserRoles" ("UserId", "RoleId") SELECT @id, "Id" FROM "AspNetRoles" WHERE "NormalizedName" = 'SERVERADMIN'""", new { id });
        return id;
    }

    private static async Task<FullBuildId> AddVersionBuild(NpgsqlConnection conn, PluginSlug slug, char commit,
        string? directory = null, string? version = null, bool preRelease = false)
    {
        var (id, manifest) = await AddUploadedBuild(conn, slug, commit, directory, version);
        Assert.True(await conn.EnsureIdentifierOwnership(slug, manifest.Identifier));
        Assert.True(await conn.SetVersionBuild(id, manifest.Version, manifest.BTCPayMinVersion, manifest.BTCPayMaxVersion, preRelease));
        return id;
    }

    private static async Task<(FullBuildId Id, PluginManifest Manifest)> AddUploadedBuild(NpgsqlConnection conn,
        PluginSlug slug, char commit, string? directory = null, string? version = null, string? identifier = null)
    {
        var id = new FullBuildId(slug, await conn.NewBuild(slug, new PluginBuildParameters("https://github.com/example/plugin") { PluginDirectory = directory }));
        var manifest = PluginManifest.Parse(new JObject
        {
            ["Identifier"] = identifier ?? "TestPlugin." + slug.ToString().Replace('-', '_'),
            ["Name"] = "Listing test plugin", ["Description"] = "Version-backed listing fixture",
            ["Version"] = version ?? $"1.0.{id.BuildId}",
            ["Dependencies"] = new JArray(new JObject { ["Identifier"] = "BTCPayServer", ["Condition"] = ">=1.4.6.0" })
        }.ToString());
        await conn.UpdateBuild(id, BuildStates.Uploaded, new JObject { ["gitCommit"] = new string(commit, 40) }, manifest);
        return (id, manifest);
    }
}
