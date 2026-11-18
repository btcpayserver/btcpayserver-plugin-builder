using Dapper;
using Microsoft.AspNetCore.Identity;
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
public class GithubManualBanTests(ITestOutputHelper output) : UnitTestBase(output)
{
    [Theory]
    [InlineData(" 000321 ", "321")]
    [InlineData("MiXeD-Name", "@mixed-name")]
    [InlineData(" @123 ", "@123")]
    [InlineData("0", null)]
    [InlineData("9223372036854775808", null)]
    [InlineData("https://github.com/name", null)]
    [InlineData("@-bad", null)]
    [InlineData("bad--name", null)]
    [InlineData(null, null)]
    public void TargetsDistinguishStableIdsFromUsernames(string? value, string? expected) =>
        Assert.Equal(expected, GithubIdentityService.NormalizeTarget(value));

    [Fact]
    public async Task ManualBansUseExplicitTargetsAndPreserveDuplicateMetadata()
    {
        await using var tester = await StartServer();
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        var owner = await tester.CreateFakeUserAsync(githubVerified: false);
        await SetIdentity(conn, owner, 321, "Known-Owner");
        var service = tester.GetService<GithubIdentityService>();

        var stableId = await service.Create("321", " first reason ", "original-admin", "original-token");
        var stableSnapshot = await Snapshot(conn, stableId);
        Assert.Equal(stableId, await service.Create("000321", "replacement reason", "another-admin", "another-token"));
        Assert.Equal(stableSnapshot, await Snapshot(conn, stableId));
        Assert.Equal(321, await conn.QuerySingleAsync<long?>("SELECT github_user_id FROM github_bans WHERE id = @stableId", new { stableId }));
        Assert.Equal("first reason", await conn.QuerySingleAsync<string>("SELECT reason FROM github_bans WHERE id = @stableId", new { stableId }));

        // A locally stored proof can have an old username after a rename; do not infer its numeric ID.
        var usernameId = await service.Create("@Known-Owner", "username reason", "original-admin", null);
        Assert.NotEqual(stableId, usernameId);
        var usernameSnapshot = await Snapshot(conn, usernameId);
        Assert.Equal(usernameId, await service.Create("KNOWN-OWNER", "replacement reason", "another-admin", null));
        Assert.Equal(usernameSnapshot, await Snapshot(conn, usernameId));
        Assert.Null(await conn.QuerySingleAsync<long?>("SELECT github_user_id FROM github_bans WHERE id = @usernameId", new { usernameId }));
        Assert.Equal("known-owner", await conn.QuerySingleAsync<string>("SELECT github_login FROM github_bans WHERE id = @usernameId", new { usernameId }));
        Assert.Equal(2, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM github_bans"));
        Assert.Equal(2, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_events WHERE type = 'github.banned'"));
        var audit = JObject.Parse(await conn.QuerySingleAsync<string>("""
            SELECT data::text FROM admin_events WHERE type = 'github.banned' AND data->>'banId' = @id
            """, new { id = stableId.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
        Assert.Equal("original-admin", audit.Value<string>("by"));
        Assert.Equal("original-token", audit.Value<string>("tokenId"));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_account_locks"));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM ip_bans"));
    }

    [Fact]
    public async Task KnownServerAdminIdentitiesCannotBeBannedByIdOrUsername()
    {
        await using var tester = await StartServer();
        var adminId = await tester.CreateFakeUserAsync(githubVerified: false);
        using (var scope = tester.WebApp.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var admin = (await users.FindByIdAsync(adminId))!;
            Assert.True((await users.AddToRoleAsync(admin, Roles.ServerAdmin)).Succeeded);
        }
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await SetIdentity(conn, adminId, 777, "Admin-Owner");
        var service = tester.GetService<GithubIdentityService>();
        foreach (var target in new[] { "777", "@ADMIN-OWNER" })
            await Assert.ThrowsAsync<GithubIdentityService.ProtectedIdentityException>(() => service.Create(target, "test", "actor", null));

        // A verified legacy username without a recorded numeric ID has the same protection.
        await SetIdentity(conn, adminId, null, "Admin-Owner");
        await Assert.ThrowsAsync<GithubIdentityService.ProtectedIdentityException>(() => service.Create("admin-owner", "test", "actor", null));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM github_bans"));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_events WHERE type = 'github.banned'"));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_account_locks"));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM ip_bans"));
    }

    [Fact]
    public async Task CreationLeavesAccountsAloneUntilBuildEnforcementAndRevocationDoesNotUnlockThem()
    {
        await using var tester = await StartServer();
        var owner = await tester.CreateFakeUserAsync(githubVerified: false);
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await SetIdentity(conn, owner, 4242, "Build-Owner");
        var slug = new PluginSlug("manual-github-ban");
        await conn.NewPlugin(slug, owner);
        var buildId = await conn.NewBuild(slug, new PluginBuildParameters(ServerTester.RepoUrl), owner);
        var service = tester.GetService<GithubIdentityService>();

        var banId = await service.Create("4242", "identity ban", "actor", null);
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_account_locks"));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM ip_bans"));
        Assert.Equal("queued", await conn.QuerySingleAsync<string>(
            "SELECT state FROM builds WHERE plugin_slug = @slug AND id = @buildId", new { slug = slug.ToString(), buildId }));

        Assert.Equal(GithubIdentityService.SuspendedMessage, await service.GetBuildRestriction(owner, true));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_account_locks WHERE user_id = @owner", new { owner }));
        Assert.Equal("failed", await conn.QuerySingleAsync<string>(
            "SELECT state FROM builds WHERE plugin_slug = @slug AND id = @buildId", new { slug = slug.ToString(), buildId }));

        Assert.True(await service.Revoke(banId, "reviewed", "actor", null));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM github_bans"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_account_locks WHERE user_id = @owner", new { owner }));
        Assert.Equal(GithubIdentityService.SuspendedMessage, await service.GetBuildRestriction(owner, true));
        Assert.Equal(AccountModerationService.Outcome.Completed,
            await tester.GetService<AccountModerationService>().Unlock(owner, "separate review", "actor", null));
        Assert.Null(await service.GetBuildRestriction(owner, true));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM ip_bans"));
    }

    private async Task<ServerTester> StartServer()
    {
        var tester = Create(nameof(GithubManualBanTests));
        tester.ReuseDatabase = false;
        tester.ConfigureServices = services =>
        {
            foreach (var descriptor in services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                         && descriptor.ImplementationType != typeof(DatabaseStartupHostedService)).ToArray())
                services.Remove(descriptor);
        };
        await tester.Start();
        return tester;
    }

    private static Task SetIdentity(NpgsqlConnection conn, string userId, long? id, string login) =>
        conn.ExecuteAsync("""
            UPDATE "AspNetUsers" SET "GithubUserId" = @id,
                "GithubGistUrl" = @proof,
                "AccountDetail" = jsonb_build_object('github', @login::text)
            WHERE "Id" = @userId
            """, new { userId, id, login, proof = $"https://gist.github.com/{login}/abcdef" });

    private static Task<string> Snapshot(NpgsqlConnection conn, long id) =>
        conn.QuerySingleAsync<string>("SELECT to_jsonb(b)::text FROM github_bans b WHERE id = @id", new { id });
}
