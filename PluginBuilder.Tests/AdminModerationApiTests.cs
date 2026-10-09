using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json.Linq;
using Npgsql;
using PluginBuilder.Controllers;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.DataModels;
using PluginBuilder.HostedServices;
using PluginBuilder.Services;
using PluginBuilder.Util;
using PluginBuilder.Util.Extensions;
using PluginBuilder.Builds.Services;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests;

// BuildService's execution slots are process-wide, so the tests that run builds must not overlap other build tests.
[Collection(nameof(NonParallelizableCollectionDefinition))]
public class AdminModerationApiTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    private const string Password = "test-password:with-colons:123";
    private const string Repository = "https://github.com/example/moderation-plugin";

    [Fact]
    public async Task LockEndsAccessCancelsUnfinishedBuildsAndUnlockRestoresIt()
    {
        await using var tester = await StartAdminServer();
        var (admin, owner) = await AdminAndOwner(tester);
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        var slug = new PluginSlug("moderation-lock");
        await conn.NewPlugin(slug, owner.Id);
        var queued = await conn.NewBuild(slug, new PluginBuildParameters(Repository), owner.Id);
        var running = await conn.NewBuild(slug, new PluginBuildParameters(Repository), owner.Id);
        await conn.UpdateBuild(new FullBuildId(slug, running), BuildStates.Running, null);
        var finished = await conn.NewBuild(slug, new PluginBuildParameters(Repository), owner.Id);
        await conn.UpdateBuild(new FullBuildId(slug, finished), BuildStates.Uploaded, null);
        var stampBefore = await Stamp(conn, owner.Id);

        using var ownerClient = tester.CreateHttpClient().SetBasicAuth(owner.Email!, Password);
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.GetAsync($"/api/v1/plugins/{slug}/builds")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ownerClient.PostAsJsonAsync($"/api/v1/admin/users/{owner.Id}/lock", new { reason = "self" })).StatusCode);

        var (adminClient, tokenId) = await TokenClient(tester, admin);
        Assert.Equal(HttpStatusCode.BadRequest, (await adminClient.PostAsJsonAsync($"/api/v1/admin/users/{owner.Id}/lock", new { reason = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await adminClient.PostAsJsonAsync($"/api/v1/admin/users/{owner.Id}/lock",
            new { reason = "past", until = DateTimeOffset.UtcNow.AddMinutes(-1) })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminClient.PostAsJsonAsync("/api/v1/admin/users/no-such-user/lock", new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await adminClient.PostAsJsonAsync($"/api/v1/admin/users/{admin.Id}/lock", new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await adminClient.PostAsJsonAsync($"/api/v1/admin/users/{owner.Id}/unlock", new { reason = "not locked" })).StatusCode);
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_events WHERE type IN ('user.locked', 'build.cancelled')"));

        var locked = await adminClient.PostAsJsonAsync($"/api/v1/admin/users/{owner.Id}/lock", new { reason = "scripted sign-up burst" });
        Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
        var lockedUser = JObject.Parse(await locked.Content.ReadAsStringAsync());
        Assert.True(lockedUser.Value<bool>("lockoutEnabled"));
        // An indefinite lock is stored as Postgres infinity, the same as Identity's own DateTimeOffset.MaxValue lockout.
        Assert.Equal("infinity", lockedUser.Value<string>("lockoutEnd"));
        Assert.Equal("scripted sign-up burst", lockedUser["adminLock"]!.Value<string>("reason"));
        Assert.Equal(admin.Id, lockedUser["adminLock"]!.Value<string>("lockedBy"));
        var listed = JObject.Parse(await adminClient.GetStringAsync($"/api/v1/admin/users/{owner.Id}"));
        Assert.Equal("scripted sign-up burst", listed["adminLock"]!.Value<string>("reason"));

        // The account can no longer use the API, and anything bound to its old security stamp is void.
        Assert.Equal(HttpStatusCode.Unauthorized, (await ownerClient.GetAsync($"/api/v1/plugins/{slug}/builds")).StatusCode);
        Assert.NotEqual(stampBefore, await Stamp(conn, owner.Id));

        foreach (var id in new[] { queued, running })
        {
            Assert.Equal("failed", await State(conn, slug, id));
            Assert.Equal(AdminModerationController.CancelledByLock, await Error(conn, slug, id));
        }
        Assert.Equal("uploaded", await State(conn, slug, finished));

        var events = (await conn.QueryAsync<(long Id, string Type, string Data)>(
            "SELECT id, type, data::text FROM admin_events WHERE type IN ('user.locked', 'build.cancelled', 'build.failed') ORDER BY id")).ToList();
        Assert.Equal(new[] { "user.locked", "build.cancelled", "build.failed", "build.cancelled", "build.failed" }, events.Select(e => e.Type));
        var lockEvent = JObject.Parse(events[0].Data);
        Assert.Equal(owner.Id, lockEvent.Value<string>("userId"));
        Assert.Equal("scripted sign-up burst", lockEvent.Value<string>("reason"));
        Assert.Equal(admin.Id, lockEvent.Value<string>("by"));
        Assert.Equal(tokenId, lockEvent.Value<string>("tokenId"));
        Assert.Equal(new[] { queued, running }, lockEvent["cancelledBuilds"]!.Select(b => b.Value<long>("buildId")));
        var cancelEvent = JObject.Parse(events[1].Data);
        Assert.Equal("queued", cancelEvent.Value<string>("previousState"));
        Assert.Equal("Account locked: scripted sign-up burst", cancelEvent.Value<string>("reason"));

        Assert.Equal(HttpStatusCode.BadRequest, (await adminClient.PostAsJsonAsync($"/api/v1/admin/users/{owner.Id}/unlock", new { reason = "" })).StatusCode);
        var unlocked = await adminClient.PostAsJsonAsync($"/api/v1/admin/users/{owner.Id}/unlock", new { reason = "false positive" });
        Assert.Equal(HttpStatusCode.OK, unlocked.StatusCode);
        var unlockedUser = JObject.Parse(await unlocked.Content.ReadAsStringAsync());
        Assert.Equal(JTokenType.Null, unlockedUser["lockoutEnd"]!.Type);
        Assert.Equal(JTokenType.Null, unlockedUser["adminLock"]!.Type);
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_account_locks"));
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.GetAsync($"/api/v1/plugins/{slug}/builds")).StatusCode);
        var unlockEvent = JObject.Parse(await conn.ExecuteScalarAsync<string>("SELECT data::text FROM admin_events WHERE type = 'user.unlocked'"));
        Assert.Equal("false positive", unlockEvent.Value<string>("reason"));
        Assert.Equal(admin.Id, unlockEvent.Value<string>("by"));
        Assert.Equal(HttpStatusCode.Conflict, (await adminClient.PostAsJsonAsync($"/api/v1/admin/users/{owner.Id}/unlock", new { reason = "again" })).StatusCode);
    }

    [Fact]
    public async Task ATimedLockExpiresOnItsOwn()
    {
        var sandbox = new AdmissionTestSandbox();
        await using var tester = await StartBuildServer(sandbox);
        var (admin, owner) = await AdminAndOwner(tester);
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        var slug = new PluginSlug("moderation-timed-lock");
        await conn.NewPlugin(slug, owner.Id);
        var (adminClient, _) = await TokenClient(tester, admin);
        var until = DateTimeOffset.UtcNow.AddSeconds(3);
        Assert.Equal(HttpStatusCode.OK, (await adminClient.PostAsJsonAsync($"/api/v1/admin/users/{owner.Id}/lock", new { reason = "cool down", until })).StatusCode);
        using var ownerClient = tester.CreateHttpClient().SetBasicAuth(owner.Email!, Password);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ownerClient.GetAsync($"/api/v1/plugins/{slug}/builds")).StatusCode);
        Assert.Equal(JTokenType.Object, JObject.Parse(await adminClient.GetStringAsync($"/api/v1/admin/users/{owner.Id}"))["adminLock"]!.Type);

        await Task.Delay(until - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(500));
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.GetAsync($"/api/v1/plugins/{slug}/builds")).StatusCode);
        Assert.Equal(JTokenType.Null, JObject.Parse(await adminClient.GetStringAsync($"/api/v1/admin/users/{owner.Id}"))["adminLock"]!.Type);
        // The executor no longer refuses the account: the build reaches the sandbox (which fails every build it prepares).
        var buildId = await conn.NewBuild(slug, new PluginBuildParameters(Repository), owner.Id);
        await Assert.ThrowsAsync<BuildServiceException>(() =>
            tester.GetService<BuildService>().Build(new FullBuildId(slug, buildId), false).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(sandbox.StartedPreparations.ContainsKey(new FullBuildId(slug, buildId)));
    }

    [Fact]
    public async Task CancelStopsOnlyUnfinishedBuildsAndRecordsWhy()
    {
        await using var tester = await StartAdminServer();
        var (admin, owner) = await AdminAndOwner(tester);
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        var slug = new PluginSlug("moderation-cancel");
        await conn.NewPlugin(slug, owner.Id);
        var queued = await conn.NewBuild(slug, new PluginBuildParameters(Repository), owner.Id);
        var finished = await conn.NewBuild(slug, new PluginBuildParameters(Repository), owner.Id);
        await conn.UpdateBuild(new FullBuildId(slug, finished), BuildStates.Uploaded, null);
        var (adminClient, tokenId) = await TokenClient(tester, admin);
        var url = $"/api/v1/admin/plugins/{slug}/builds/{queued}/cancel";

        Assert.Equal(HttpStatusCode.BadRequest, (await adminClient.PostAsJsonAsync(url, new { reason = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminClient.PostAsJsonAsync($"/api/v1/admin/plugins/{slug}/builds/999/cancel", new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await adminClient.PostAsJsonAsync($"/api/v1/admin/plugins/{slug}/builds/{finished}/cancel", new { reason = "x" })).StatusCode);
        using var ownerClient = tester.CreateHttpClient().SetBasicAuth(owner.Email!, Password);
        Assert.Equal(HttpStatusCode.Forbidden, (await ownerClient.PostAsJsonAsync(url, new { reason = "x" })).StatusCode);
        Assert.Equal("queued", await State(conn, slug, queued));

        var response = await adminClient.PostAsJsonAsync(url, new { reason = "Exec in csproj" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var build = JObject.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("failed", build.Value<string>("state"));
        Assert.Equal(AdminModerationController.CancelledByAdmin, build["buildInfo"]!.Value<string>("error"));
        Assert.Equal(HttpStatusCode.Conflict, (await adminClient.PostAsJsonAsync(url, new { reason = "again" })).StatusCode);

        var events = (await conn.QueryAsync<(string Type, string Data)>(
            "SELECT type, data::text FROM admin_events WHERE type IN ('build.cancelled', 'build.failed') ORDER BY id")).ToList();
        Assert.Equal(new[] { "build.cancelled", "build.failed" }, events.Select(e => e.Type));
        var cancelled = JObject.Parse(events[0].Data);
        Assert.Equal(slug.ToString(), cancelled.Value<string>("pluginSlug"));
        Assert.Equal(queued, cancelled.Value<long>("buildId"));
        Assert.Equal("queued", cancelled.Value<string>("previousState"));
        Assert.Equal("Exec in csproj", cancelled.Value<string>("reason"));
        Assert.Equal(admin.Id, cancelled.Value<string>("by"));
        Assert.Equal(tokenId, cancelled.Value<string>("tokenId"));
    }

    [Fact]
    public async Task CancellingABuildInFlightStopsItWithoutPublishingOrOverwritingTheReason()
    {
        TaskCompletionSource neverReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var sandbox = new AdmissionTestSandbox { PreparationBlockedUntil = neverReleased.Task };
        await using var tester = await StartBuildServer(sandbox);
        var (admin, owner) = await AdminAndOwner(tester);
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        var slug = new PluginSlug("moderation-inflight");
        await conn.NewPlugin(slug, owner.Id);
        var buildId = await conn.NewBuild(slug, new PluginBuildParameters(Repository), owner.Id);
        var fullBuildId = new FullBuildId(slug, buildId);

        var run = tester.GetService<BuildService>().Build(fullBuildId, false);
        await sandbox.WaitForPreparationStartsAsync(1);
        var (adminClient, _) = await TokenClient(tester, admin);
        Assert.Equal(HttpStatusCode.OK,
            (await adminClient.PostAsJsonAsync($"/api/v1/admin/plugins/{slug}/builds/{buildId}/cancel", new { reason = "suspicious" })).StatusCode);

        // The pipeline stops waiting on the sandbox and returns normally; the recorded reason survives.
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("failed", await State(conn, slug, buildId));
        Assert.Equal(AdminModerationController.CancelledByAdmin, await Error(conn, slug, buildId));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM versions WHERE plugin_slug = @slug", new { slug = slug.ToString() }));
        Assert.False(tester.GetService<BuildCancellationRegistry>().Cancel(fullBuildId));
    }

    [Fact]
    public async Task ACancellationThatLandsAfterPreparationIsNotUndoneByThePipeline()
    {
        // The cancel commits while the sandbox is preparing, and the sandbox returns without watching the token:
        // the pipeline's next update ("running") must not revive the cancelled build.
        var sandbox = new CancelDuringPreparationSandbox();
        await using var tester = await StartBuildServer(services =>
        {
            services.RemoveAll<IBuildSandbox>();
            services.AddSingleton<IBuildSandbox>(sandbox);
        }, nameof(ACancellationThatLandsAfterPreparationIsNotUndoneByThePipeline));
        var (admin, owner) = await AdminAndOwner(tester);
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        var slug = new PluginSlug("moderation-late-cancel");
        await conn.NewPlugin(slug, owner.Id);
        var buildId = await conn.NewBuild(slug, new PluginBuildParameters(Repository), owner.Id);
        var (adminClient, _) = await TokenClient(tester, admin);
        sandbox.OnPrepare = async () => Assert.Equal(HttpStatusCode.OK,
            (await adminClient.PostAsJsonAsync($"/api/v1/admin/plugins/{slug}/builds/{buildId}/cancel", new { reason = "late" })).StatusCode);

        await tester.GetService<BuildService>().Build(new FullBuildId(slug, buildId), false).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(sandbox.Prepared);
        Assert.Equal("failed", await State(conn, slug, buildId));
        Assert.Equal(AdminModerationController.CancelledByAdmin, await Error(conn, slug, buildId));
    }

    private sealed class CancelDuringPreparationSandbox : IBuildSandbox
    {
        public Func<Task> OnPrepare { get; set; } = () => Task.CompletedTask;
        public bool Prepared { get; private set; }

        public async Task<IPreparedBuild> PrepareAsync(FullBuildId buildId, BuildInfo buildInfo, CancellationToken cancellationToken = default)
        {
            await OnPrepare();
            Prepared = true;
            return new AdmissionTestSandbox.PreparedBuild();
        }
    }

    [Fact]
    public async Task ABuildCancelledBeforeItsPipelineStartsNeverReachesTheSandbox()
    {
        var sandbox = new AdmissionTestSandbox();
        await using var tester = await StartBuildServer(sandbox);
        var (admin, owner) = await AdminAndOwner(tester);
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        var slug = new PluginSlug("moderation-early-cancel");
        await conn.NewPlugin(slug, owner.Id);
        var buildId = await conn.NewBuild(slug, new PluginBuildParameters(Repository), owner.Id);
        var (adminClient, _) = await TokenClient(tester, admin);
        Assert.Equal(HttpStatusCode.OK,
            (await adminClient.PostAsJsonAsync($"/api/v1/admin/plugins/{slug}/builds/{buildId}/cancel", new { reason = "early" })).StatusCode);

        await tester.GetService<BuildService>().Build(new FullBuildId(slug, buildId), false).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(sandbox.StartedPreparations);
        Assert.Equal("failed", await State(conn, slug, buildId));
        Assert.Equal(AdminModerationController.CancelledByAdmin, await Error(conn, slug, buildId));
    }

    [Fact]
    public async Task ALockedAccountsBuildIsRefusedBeforeItReachesTheSandbox()
    {
        var sandbox = new AdmissionTestSandbox();
        await using var tester = await StartBuildServer(sandbox);
        var (admin, owner) = await AdminAndOwner(tester);
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        var slug = new PluginSlug("moderation-locked-build");
        await conn.NewPlugin(slug, owner.Id);
        var (adminClient, _) = await TokenClient(tester, admin);
        Assert.Equal(HttpStatusCode.OK, (await adminClient.PostAsJsonAsync($"/api/v1/admin/users/{owner.Id}/lock", new { reason = "x" })).StatusCode);

        // A browser session can outlive the lock until its security stamp is revalidated; the executor checks again.
        var buildId = await conn.NewBuild(slug, new PluginBuildParameters(Repository), owner.Id);
        await tester.GetService<BuildService>().Build(new FullBuildId(slug, buildId), false).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("failed", await State(conn, slug, buildId));
        Assert.Equal("Builds by this account are suspended by a server admin.", await Error(conn, slug, buildId));
        Assert.Empty(sandbox.StartedPreparations);
    }

    [Fact]
    public async Task AFailedLoginLockoutIsNotAnAdminSuspension()
    {
        // Anyone who knows an account's email can trip Identity's lockout with bad passwords; that must not fail the
        // account's builds or read as a moderation action.
        var sandbox = new AdmissionTestSandbox();
        await using var tester = await StartBuildServer(sandbox);
        var (admin, owner) = await AdminAndOwner(tester);
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        var slug = new PluginSlug("moderation-failed-logins");
        await conn.NewPlugin(slug, owner.Id);
        using (var attacker = tester.CreateHttpClient().SetBasicAuth(owner.Email!, "wrong-password"))
            for (var i = 0; i < 5; i++)
                Assert.Equal(HttpStatusCode.Unauthorized, (await attacker.GetAsync($"/api/v1/plugins/{slug}/builds")).StatusCode);
        Assert.True(await conn.ExecuteScalarAsync<bool>(
            "SELECT \"LockoutEnd\" > CURRENT_TIMESTAMP FROM \"AspNetUsers\" WHERE \"Id\" = @id", new { id = owner.Id }));

        var (adminClient, _) = await TokenClient(tester, admin);
        var user = JObject.Parse(await adminClient.GetStringAsync($"/api/v1/admin/users/{owner.Id}"));
        Assert.Equal(JTokenType.Null, user["adminLock"]!.Type);

        // The test sandbox fails every build once it is prepared; reaching it is what shows the build was not refused.
        var buildId = await conn.NewBuild(slug, new PluginBuildParameters(Repository), owner.Id);
        await Assert.ThrowsAsync<BuildServiceException>(() =>
            tester.GetService<BuildService>().Build(new FullBuildId(slug, buildId), false).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(sandbox.StartedPreparations.ContainsKey(new FullBuildId(slug, buildId)));
        Assert.Equal("Plugin build failed.", await Error(conn, slug, buildId));

        // An admin may still lift the failed-login lockout early.
        Assert.Equal(HttpStatusCode.OK, (await adminClient.PostAsJsonAsync($"/api/v1/admin/users/{owner.Id}/unlock", new { reason = "owner confirmed" })).StatusCode);
        using var ownerClient = tester.CreateHttpClient().SetBasicAuth(owner.Email!, Password);
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.GetAsync($"/api/v1/plugins/{slug}/builds")).StatusCode);
    }

    [Fact]
    public async Task ThePipelinesStateUpdateNeverRevivesAFinishedBuild()
    {
        await using var tester = await StartAdminServer();
        var (_, owner) = await AdminAndOwner(tester);
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        var slug = new PluginSlug("moderation-guard");
        await conn.NewPlugin(slug, owner.Id);
        var id = new FullBuildId(slug, await conn.NewBuild(slug, new PluginBuildParameters(Repository), owner.Id));
        Assert.True(await conn.UpdateUnfinishedBuild(id, BuildStates.Running, null));
        await conn.UpdateBuild(id, BuildStates.Failed, new JObject { ["error"] = "cancelled" });
        foreach (var state in new[] { BuildStates.Running, BuildStates.Uploading, BuildStates.Uploaded, BuildStates.Failed })
            Assert.False(await conn.UpdateUnfinishedBuild(id, state, new JObject { ["error"] = "overwritten" }));
        Assert.Equal("failed", await State(conn, slug, id.BuildId));
        Assert.Equal("cancelled", await Error(conn, slug, id.BuildId));
    }

    private async Task<ServerTester> StartAdminServer()
    {
        var tester = Create();
        tester.ReuseDatabase = false;
        tester.ConfigureServices = services =>
        {
            foreach (var service in services.Where(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType != typeof(DatabaseStartupHostedService)).ToArray())
                services.Remove(service);
        };
        await tester.Start();
        return tester;
    }

    private Task<ServerTester> StartBuildServer(AdmissionTestSandbox sandbox, [System.Runtime.CompilerServices.CallerMemberName] string? caller = null) =>
        StartBuildServer(sandbox.Register, caller);

    private async Task<ServerTester> StartBuildServer(Action<IServiceCollection> registerSandbox, string? caller)
    {
        var tester = Create(caller);
        tester.ReuseDatabase = false;
        tester.ConfigureServices = services =>
        {
            registerSandbox(services);
            // These builds never reach the artifact upload, so no hosted service but the database is needed.
            foreach (var service in services.Where(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType != typeof(DatabaseStartupHostedService)).ToArray())
                services.Remove(service);
        };
        await tester.Start();
        tester.GetService<BuildExecutorState>().MarkReady(
            "sha256:1111111111111111111111111111111111111111111111111111111111111111",
            "sha256:2222222222222222222222222222222222222222222222222222222222222222");
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.SettingsSetAsync(SettingsKeys.NewBuildsEnabled, "true");
        await tester.GetService<AdminSettingsCache>().RefreshFeatureSettings(conn);
        return tester;
    }

    private static async Task<(IdentityUser Admin, IdentityUser Owner)> AdminAndOwner(ServerTester tester)
    {
        using var scope = tester.WebApp.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        return (await AddUser(users, "admin@moderation.test", true), await AddUser(users, "owner@moderation.test"));
    }

    private static async Task<IdentityUser> AddUser(UserManager<IdentityUser> users, string email, bool admin = false)
    {
        var user = new IdentityUser { UserName = email, Email = email, EmailConfirmed = true };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        if (admin)
            Assert.True((await users.AddToRoleAsync(user, Roles.ServerAdmin)).Succeeded);
        return user;
    }

    private static async Task<(HttpClient Client, string TokenId)> TokenClient(ServerTester tester, IdentityUser admin)
    {
        using var issuer = tester.CreateHttpClient().SetBasicAuth(admin.Email!, Password);
        var response = await issuer.PostAsJsonAsync("/api/v1/admin/access-tokens", new { name = "moderation test", expiresInDays = 1 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var token = JObject.Parse(await response.Content.ReadAsStringAsync());
        var client = tester.CreateHttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value<string>("token"));
        return (client, token.Value<string>("id")!);
    }

    private static Task<string?> Stamp(NpgsqlConnection conn, string userId) =>
        conn.ExecuteScalarAsync<string?>("SELECT \"SecurityStamp\" FROM \"AspNetUsers\" WHERE \"Id\" = @userId", new { userId });

    private static Task<string?> State(NpgsqlConnection conn, PluginSlug slug, long id) =>
        conn.ExecuteScalarAsync<string?>("SELECT state FROM builds WHERE plugin_slug = @slug AND id = @id", new { slug = slug.ToString(), id });

    private static Task<string?> Error(NpgsqlConnection conn, PluginSlug slug, long id) =>
        conn.ExecuteScalarAsync<string?>("SELECT build_info->>'error' FROM builds WHERE plugin_slug = @slug AND id = @id", new { slug = slug.ToString(), id });
}
