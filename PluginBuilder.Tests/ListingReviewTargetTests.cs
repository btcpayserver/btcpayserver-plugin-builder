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
    [InlineData(null)]
    [InlineData("src/Plugin")]
    public async Task OwnerSubmissionPinsBuildSourceInRequestEventAndAdminReads(string? pluginDirectory)
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
        Assert.Contains("A successful build", page);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["ReleaseNote"] = "Release", ["UserReviews"] = "Reviewed", ["TelegramVerificationMessage"] = "https://t.me/btcpayserver/123",
            ["HasSuccessfulBuild"] = "true", ["ReviewTarget.BuildId"] = "999",
            ["ReviewTarget.Repository"] = "https://github.com/forged/repo", ["ReviewTarget.GitCommit"] = new string('f', 40),
            ["ReviewTarget.PluginDirectory"] = "src/Forged",
            ["__RequestVerificationToken"] = HttpTestHelpers.AntiforgeryToken(page)
        });
        using var missing = await browser.PostAsync(path, form);
        Assert.Equal(HttpStatusCode.OK, missing.StatusCode);
        Assert.Contains("A successful build", await missing.Content.ReadAsStringAsync());
        Assert.False(await conn.HasPendingListingRequest(slug));

        var build = await AddBuild(conn, slug, 'a', pluginDirectory);
        Assert.Equal(0, build.BuildId);
        var failed = new FullBuildId(slug, await conn.NewBuild(slug, new PluginBuildParameters("https://github.com/example/failed-repo")));
        await conn.UpdateBuild(failed, BuildStates.Failed, null);
        using var accepted = await browser.PostAsync(path, form);
        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        var request = (await conn.GetPendingListingRequestForPlugin(slug))!;
        var target = request.ReviewTarget!;
        Assert.Equal(build.BuildId, target.BuildId);
        Assert.Equal("https://github.com/example/plugin", target.Repository);
        Assert.Equal(new string('a', 40), target.GitCommit);
        Assert.Equal(pluginDirectory, target.PluginDirectory);
        Assert.Equal(PluginListingRequestStatus.Pending, request.Status);

        await AddBuild(conn, slug, 'b', "src/Other");
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
        using var adminBrowser = HttpTestHelpers.CreateBrowser(server);
        await HttpTestHelpers.LogIn(adminBrowser, "listing-admin@example.com", "123456");
        var adminPage = await adminBrowser.GetStringAsync($"/admin/listing-requests/{request.Id}");
        Assert.Contains(target.GitCommit, adminPage);
        Assert.Contains(target.Repository, adminPage);
        Assert.Contains(pluginDirectory ?? "Repository root", adminPage);
    }

    [Theory]
    [InlineData("gitCommit", "main")]
    [InlineData("gitRepository", "http://github.com/example/plugin")]
    [InlineData("pluginDir", "../Other")]
    public async Task InvalidLatestUploadedBuildDoesNotFallBackToOlderSource(string field, string value)
    {
        await using var migration = CreateMigrationTester("ListingTargetSelection");
        await migration.RunScriptsUntil("34.ListingReviewTarget");
        await migration.RunRemainingScripts();
        await using var conn = await migration.Open();
        var slug = new PluginSlug("listing-invalid-source");
        await conn.ExecuteAsync("INSERT INTO plugins(slug) VALUES (@slug)", new { slug = slug.ToString() });
        await AddBuild(conn, slug, 'a');
        var latest = await AddBuild(conn, slug, 'b');
        await conn.UpdateBuild(latest, BuildStates.Uploaded, new JObject { [field] = value });
        Assert.Null(await conn.GetLatestListingReviewTarget(slug));
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

        await using (var transaction = await conn.BeginTransactionAsync())
        {
            await conn.CreateListingRequest("listing-legacy", "Release", "Proof", "Reviews", null,
                reviewTarget: await conn.GetLatestListingReviewTarget("listing-legacy"));
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
        var requestId = await conn.CreateListingRequest(slug, "Release", "Proof", "Reviews", null, admin);
        await AddBuild(conn, slug, 'a');
        using var api = server.CreateHttpClient();
        api.SetBasicAuth("listing-admin@example.com", "123456");
        var detail = JObject.Parse(await api.GetStringAsync($"/api/v1/admin/listing-requests/{requestId}"));
        Assert.Equal(JTokenType.Null, detail["reviewTarget"]!.Type);
        using var browser = HttpTestHelpers.CreateBrowser(server);
        await HttpTestHelpers.LogIn(browser, "listing-admin@example.com", "123456");
        var page = await browser.GetStringAsync($"/admin/listing-requests/{requestId}");
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

    private static async Task<FullBuildId> AddBuild(NpgsqlConnection conn, PluginSlug slug, char commit, string? directory = null)
    {
        var id = new FullBuildId(slug, await conn.NewBuild(slug, new PluginBuildParameters("https://github.com/example/plugin") { PluginDirectory = directory }));
        await conn.UpdateBuild(id, BuildStates.Uploaded, new JObject { ["gitCommit"] = new string(commit, 40) });
        return id;
    }
}
