using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Newtonsoft.Json.Linq;
using PluginBuilder.Authentication;
using PluginBuilder.Controllers;
using PluginBuilder.HostedServices;
using PluginBuilder.Services;
using PluginBuilder.Util;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests;

public class AdminWebhookTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.100.100.200")]
    [InlineData("168.63.129.16")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("fd00:ec2::254")]
    [InlineData("fe80::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2002:7f00:1::1")]
    public void BlocksNonPublicAddresses(string address) => Assert.False(AdminWebhookSender.IsPublicAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("192.2.1.1")]
    [InlineData("2606:4700:4700::1111")]
    public void AllowsPublicAddresses(string address) => Assert.True(AdminWebhookSender.IsPublicAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData("http://example.com/hook")]
    [InlineData("https://user:secret@example.com/hook")]
    [InlineData("https://example.com/hook#fragment")]
    [InlineData("https://127.0.0.1/hook")]
    [InlineData("https://[::1]/hook")]
    [InlineData("file:///etc/passwd")]
    public void RejectsUnsafeDestinations(string destination) => Assert.False(AdminWebhookSender.IsValidDestination(destination));

    [Fact]
    public void SignatureBindsIdentityTimestampAndExactBody()
    {
        var key = Encoding.UTF8.GetBytes("test-signing-key");
        var secret = Convert.ToBase64String(key);
        var expected = "v1," + Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("42.123.{\"x\":1}")));
        Assert.Equal(expected, AdminWebhookSender.Sign(secret, "42", "123", "{\"x\":1}"));
        Assert.NotEqual(expected, AdminWebhookSender.Sign(secret, "43", "123", "{\"x\":1}"));
        Assert.NotEqual(expected, AdminWebhookSender.Sign(secret, "42", "124", "{\"x\":1}"));
        Assert.NotEqual(expected, AdminWebhookSender.Sign(secret, "42", "123", "{\"x\":2}"));
    }

    [Fact]
    public void AllEventEndpointsRequireAdminAuthentication()
    {
        var authorization = Assert.Single(typeof(AdminEventsController).GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>());
        Assert.Equal(Roles.ServerAdmin, authorization.Roles);
        Assert.Equal(PluginBuilderAuthenticationSchemes.AdminApi, authorization.AuthenticationSchemes);
        Assert.DoesNotContain(typeof(AdminEventsController).GetMethods(),
            m => m.GetCustomAttributes(typeof(AllowAnonymousAttribute), true).Length != 0);
    }
}

public class AdminEventEmailTests
{
    [Theory]
    [InlineData("\"a,b\"@example.test", new[] { "\"a,b\"@example.test" })]
    [InlineData("first@example.test, second@example.test", new[] { "first@example.test", "second@example.test" })]
    public async Task SendEmailPreservesMailboxBoundaries(string destination, string[] expectedRecipients)
    {
        var emails = new RecordingEmailService();
        await emails.SendEmail(destination, "Admin event", "{}");
        Assert.Equal(expectedRecipients, emails.Recipients.Select(address => Assert.IsType<MailboxAddress>(address).Address));
    }

    private sealed class RecordingEmailService() : EmailService(null!, null!, NullLogger<EmailService>.Instance)
    {
        public InternetAddress[] Recipients { get; private set; } = [];
        protected override Task<List<string>> DeliverEmail(IEnumerable<InternetAddress> toList, string subject, string messageText,
            CancellationToken cancellationToken = default)
        {
            Recipients = toList.ToArray();
            return Task.FromResult(Recipients.Select(address => address.ToString()).ToList());
        }
    }
}

public class AdminEventDatabaseTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SubscriptionDeletionPreservesSourceEvent(bool deleteFirst)
    {
        await using var tester = CreateMigrationTester("SubscriptionDeletion");
        await tester.RunScriptsUntil("25.AdminEvents");
        await tester.RunRemainingScripts();
        await using var first = await tester.Open();
        await using var second = await tester.Open();
        await using var observer = await tester.Open();
        var subscription = Guid.NewGuid();
        const string userId = "concurrent-builder";
        await observer.ExecuteAsync("""
            INSERT INTO admin_event_subscriptions(id, kind, destination, event_types, created_by)
            VALUES (@subscription, 'email', 'admin@example.test', ARRAY['user.registered'], 'admin')
            """, new { subscription });
        const string delete = "DELETE FROM admin_event_subscriptions WHERE id = @subscription";
        var args = new { subscription, id = userId };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var transaction = await first.BeginTransactionAsync(timeout.Token);
        await first.ExecuteAsync(new CommandDefinition(deleteFirst ? delete : InsertUser, args, transaction,
            cancellationToken: timeout.Token));
        if (!deleteFirst)
            Assert.Equal(1, await first.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT count(*) FROM admin_event_deliveries WHERE subscription_id = @subscription", args, transaction,
                cancellationToken: timeout.Token)));
        var competing = second.ExecuteAsync(new CommandDefinition(deleteFirst ? InsertUser : delete, args,
            cancellationToken: timeout.Token));
        try
        {
            using var waitTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            waitTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            while (!await observer.ExecuteScalarAsync<bool>(new CommandDefinition(
                       "SELECT @blockingPid = ANY(pg_blocking_pids(@waitingPid))",
                       new { blockingPid = first.ProcessID, waitingPid = second.ProcessID },
                       cancellationToken: waitTimeout.Token)))
            {
                Assert.False(competing.IsCompleted, "The competing operation must reach the held subscription lock.");
                await Task.Delay(10, waitTimeout.Token);
            }
            // Delete first: event emission skips the deleted subscription after the
            // wait. Emit first: deletion waits for the delivery's transaction.
            await transaction.CommitAsync(timeout.Token);
            Assert.Equal(1, await competing.WaitAsync(timeout.Token));
            Assert.Equal(1, await observer.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT count(*) FROM \"AspNetUsers\" WHERE \"Id\" = @id", args, cancellationToken: timeout.Token)));
            Assert.Equal(1, await observer.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT count(*) FROM admin_events WHERE type = 'user.registered' AND data->>'userId' = @id", args,
                cancellationToken: timeout.Token)));
            Assert.Equal(0, await observer.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT count(*) FROM admin_event_subscriptions", cancellationToken: timeout.Token)));
            Assert.Equal(0, await observer.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT count(*) FROM admin_event_deliveries", cancellationToken: timeout.Token)));
        }
        finally
        {
            await transaction.DisposeAsync();
            timeout.Cancel();
            try { await competing.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { /* Release and observe the competing command without masking an assertion. */ }
        }
    }

    [Fact]
    public async Task RetentionPurgesExpiredHistoryAndPreservesRecentEvidenceAndCursor()
    {
        await using var tester = CreateMigrationTester();
        await tester.RunScriptsUntil("25.AdminEvents");
        await tester.RunRemainingScripts();
        await using var conn = await tester.Open();
        await using var factory = Factory(conn.ConnectionString);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ADMIN_HISTORY_RETENTION_DAYS"] = "30"
        }).Build();
        var retention = new AdminHistoryRetention(factory, configuration);
        await conn.ExecuteAsync(InsertUser, new { id = "active" });
        await conn.ExecuteAsync("""
            INSERT INTO admin_event_subscriptions(id, kind, destination, event_types, created_by)
                VALUES (@subscription, 'email', 'admin@example.com', '{}', 'active');
            SELECT emit_admin_event('user.registered', '{"userId":"deleted-user"}');
            UPDATE admin_events SET created_at = CURRENT_TIMESTAMP - INTERVAL '31 days';
            INSERT INTO admin_api_audit(user_id, method, path, started_at) VALUES
                ('deleted-user', 'GET', '/api/v1/admin/me', CURRENT_TIMESTAMP - INTERVAL '31 days'),
                ('deleted-user', 'GET', '/api/v1/admin/me', CURRENT_TIMESTAMP);
            INSERT INTO admin_access_tokens(id, user_id, name, token_hash, expires_at) VALUES
                (@oldToken, 'active', 'expired', 'old-hash', CURRENT_TIMESTAMP - INTERVAL '31 days'),
                (@newToken, 'active', 'active', 'new-hash', CURRENT_TIMESTAMP + INTERVAL '1 day');
            INSERT INTO admin_event_first_builds VALUES ('deleted-user'), ('active');
            SELECT emit_admin_event('user.registered', '{"userId":"deleted-user","recent":true}');
            """, new { subscription = Guid.NewGuid(), oldToken = Guid.NewGuid(), newToken = Guid.NewGuid() });
        var lastId = await conn.ExecuteScalarAsync<long>("SELECT max(id) FROM admin_events");
        Assert.True(await retention.Purge(default) > 0);
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_events"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_event_deliveries"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_api_audit WHERE user_id = 'deleted-user'"));
        Assert.Equal("active", await conn.ExecuteScalarAsync<string>("SELECT name FROM admin_access_tokens"));
        Assert.Equal("active", await conn.ExecuteScalarAsync<string>("SELECT user_id FROM admin_event_first_builds"));
        Assert.Equal(0, await retention.Purge(default));
        await conn.ExecuteAsync("SELECT emit_admin_event('build.failed', '{}')");
        Assert.Equal(lastId + 1, await conn.ExecuteScalarAsync<long>("SELECT max(id) FROM admin_events"));
        await conn.ExecuteAsync("UPDATE admin_events SET created_at = CURRENT_TIMESTAMP - INTERVAL '31 days'; UPDATE admin_api_audit SET started_at = CURRENT_TIMESTAMP - INTERVAL '31 days'");
        await retention.Purge(default);
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_api_audit"));
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_event_deliveries"));
    }

    private const string InsertUser = """
        INSERT INTO "AspNetUsers" ("Id", "UserName", "Email", "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
        VALUES (@id, @id, 'builder@example.com', FALSE, FALSE, FALSE, FALSE, 0)
        """;

    [Fact]
    public async Task CapturesLifecycleAtomicallyAndPollsWithoutGaps()
    {
        await using var tester = CreateMigrationTester();
        await tester.RunScriptsUntil("25.AdminEvents");
        await tester.RunRemainingScripts();
        await using var conn = await tester.Open();
        var subscription = Guid.NewGuid();
        await conn.ExecuteAsync("""
            INSERT INTO admin_event_subscriptions(id, kind, destination, event_types, created_by)
            VALUES (@subscription, 'email', 'admin@example.com', '{}', 'admin');
            """, new { subscription });
        await conn.ExecuteAsync(InsertUser, new { id = "builder" });
        await conn.ExecuteAsync("""
            UPDATE "AspNetUsers" SET "AccountDetail" = '{"github":"builder"}', "GithubGistUrl" = 'https://gist.github.com/builder/proof' WHERE "Id" = 'builder';
            UPDATE "AspNetUsers" SET "GithubGistUrl" = 'https://gist.github.com/builder/proof' WHERE "Id" = 'builder';
            INSERT INTO plugins(slug) VALUES ('one'), ('two');
            INSERT INTO builds(plugin_slug, id, state, triggered_by, build_info) VALUES
                ('one', 0, 'queued', 'builder', '{"gitRepository":"https://github.com/example/repo","gitRef":"main"}'),
                ('two', 0, 'queued', 'builder', '{}');
            UPDATE builds SET state = 'failed' WHERE plugin_slug = 'one';
            UPDATE builds SET state = 'failed' WHERE plugin_slug = 'one';
            UPDATE builds SET state = 'uploaded' WHERE plugin_slug = 'two';
            INSERT INTO plugin_listing_requests(plugin_slug, release_note, telegram_verification_message, user_reviews, submitted_by)
            VALUES ('one', 'release', 'proof', 'reviews', 'builder');
            """);
        Assert.Equal(8, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_events"));
        Assert.Equal(8, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_event_deliveries"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_events WHERE type = 'user.first_build_triggered'"));
        Assert.Equal("builder", await conn.ExecuteScalarAsync<string>("SELECT data->>'github' FROM admin_events WHERE type = 'user.github_verified'"));
        Assert.Equal("builder", await conn.ExecuteScalarAsync<string>("SELECT data->>'userId' FROM admin_events WHERE type = 'listing.requested'"));
        Assert.False(await conn.ExecuteScalarAsync<bool>("SELECT bool_or(data ? 'email') FROM admin_events WHERE type = 'user.registered'"));

        await using (var transaction = await conn.BeginTransactionAsync())
        {
            await conn.ExecuteAsync(InsertUser, new { id = "rolled-back" }, transaction);
            await transaction.RollbackAsync();
        }
        Assert.Equal(8, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_events"));
        Assert.Equal(8, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_event_deliveries"));
        await using var factory = Factory(conn.ConnectionString);
        var service = new AdminEventService(factory);
        var page = await service.Read(0, 3, null, default);
        Assert.True(page.HasMore);
        Assert.Equal(3, page.NextCursor);
        var rest = await service.Read(page.NextCursor, 100, null, default);
        Assert.Equal(5, rest.Events.Length);
        Assert.False(rest.HasMore);
        var empty = await service.Read(rest.NextCursor, 100, null, default);
        Assert.Empty(empty.Events);
        Assert.Equal(rest.NextCursor, empty.NextCursor);
        var filtered = await service.Read(0, 100, "build.failed", default);
        Assert.Single(filtered.Events);
        // Audit history survives deletion of source rows.
        await conn.ExecuteAsync("DELETE FROM plugins; DELETE FROM \"AspNetUsers\";");
        Assert.Equal(8, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_events"));
    }

    [Fact]
    public async Task ConcurrentCommitsCannotLeapfrogPollingCursor()
    {
        await using var tester = CreateMigrationTester();
        await tester.RunScriptsUntil("25.AdminEvents");
        await tester.RunRemainingScripts();
        await using var first = await tester.Open();
        await using var second = await tester.Open();
        await using var transaction = await first.BeginTransactionAsync();
        await first.ExecuteAsync(InsertUser, new { id = "first" }, transaction);
        var competingWrite = second.ExecuteAsync(InsertUser, new { id = "second" });
        await using var factory = Factory(first.ConnectionString);
        var service = new AdminEventService(factory);
        Assert.Empty((await service.Read(0, 100, null, default)).Events);
        await transaction.CommitAsync();
        await competingWrite;
        var page = await service.Read(0, 100, null, default);
        Assert.Equal(new[] { "first", "second" }, page.Events.Select(e => e["data"]!["userId"]!.Value<string>()));
    }

    [Fact]
    public async Task FiltersSubscriptionsAndRetriesFailedDelivery()
    {
        await using var tester = CreateMigrationTester();
        await tester.RunScriptsUntil("25.AdminEvents");
        await tester.RunRemainingScripts();
        await using var conn = await tester.Open();
        await conn.ExecuteAsync("""
            INSERT INTO admin_event_subscriptions(id, kind, destination, event_types, created_by)
            VALUES (@id, 'email', 'admin@example.com', ARRAY['user.registered'], 'admin');
            SELECT emit_admin_event('build.failed', '{}');
            """, new { id = Guid.NewGuid() });
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_event_deliveries"));
        await conn.ExecuteAsync(InsertUser, new { id = "builder" });
        using var sender = new AdminWebhookSender();
        var email = new TestEmailService();
        await using var factory = Factory(conn.ConnectionString);
        var worker = new AdminEventDeliveryHostedService(factory, sender, email,
            new EphemeralDataProtectionProvider(), NullLogger<AdminEventDeliveryHostedService>.Instance);
        Assert.True(await worker.DeliverNext(default));
        Assert.Equal("pending", await conn.ExecuteScalarAsync<string>("SELECT status FROM admin_event_deliveries"));
        Assert.False(await worker.DeliverNext(default));
        email.Fail = false;
        await conn.ExecuteAsync("UPDATE admin_event_deliveries SET next_attempt_at = CURRENT_TIMESTAMP");
        Assert.True(await worker.DeliverNext(default));
        Assert.Equal("delivered", await conn.ExecuteScalarAsync<string>("SELECT status FROM admin_event_deliveries"));
        Assert.Equal(2, await conn.ExecuteScalarAsync<int>("SELECT attempts FROM admin_event_deliveries"));
        Assert.False(await worker.DeliverNext(default));
        // A disabled subscription must not send queued work; an exhausted delivery
        // stays available for inspection instead of retrying forever.
        await conn.ExecuteAsync("SELECT emit_admin_event('user.registered', '{}'); UPDATE admin_event_subscriptions SET enabled = FALSE");
        Assert.False(await worker.DeliverNext(default));
        await conn.ExecuteAsync("UPDATE admin_event_subscriptions SET enabled = TRUE; UPDATE admin_event_deliveries SET attempts = 9 WHERE status = 'pending'");
        email.Fail = true;
        Assert.True(await worker.DeliverNext(default));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_event_deliveries WHERE status = 'failed' AND attempts = 10"));
        Assert.False(await worker.DeliverNext(default));

        // Cancelling an in-flight SMTP operation releases the transaction lock
        // without consuming an attempt, so another worker can retry immediately.
        await conn.ExecuteAsync("SELECT emit_admin_event('user.registered', '{}')");
        email.Block = true;
        using var cancellation = new CancellationTokenSource();
        var sending = worker.DeliverNext(cancellation.Token);
        await email.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT attempts FROM admin_event_deliveries WHERE status = 'pending'"));
        email.Block = false;
        email.Fail = false;
        Assert.True(await worker.DeliverNext(default));
    }

    private static DBConnectionFactory Factory(string connectionString) => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["POSTGRES"] = connectionString }).Build());

    [Fact]
    public async Task MigrationPreservesHistoryAndConcurrentFirstBuildIsEmittedOnce()
    {
        await using var tester = CreateMigrationTester();
        await tester.RunScriptsUntil("25.AdminEvents");
        await using var conn = await tester.Open();
        await conn.ExecuteAsync(InsertUser, new { id = "existing" });
        await conn.ExecuteAsync("""
            INSERT INTO plugins(slug) VALUES ('old'), ('one'), ('two');
            INSERT INTO users_plugins(user_id, plugin_slug) VALUES ('existing', 'old');
            INSERT INTO builds(plugin_slug, id, state) VALUES ('old', 0, 'uploaded');
            INSERT INTO builds_logs(plugin_slug, build_id, logs, created_at) VALUES
                ('old', 0, 'historical two', '2020-01-02'), ('old', 0, 'historical one', '2020-01-01');
            INSERT INTO evts(type, data) VALUES ('Download', '{}');
            """);
        await tester.RunRemainingScripts();
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_events"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM evts"));
        var historicalLogIds = (await conn.QueryAsync<long>("SELECT id FROM builds_logs ORDER BY id")).ToArray();
        Assert.Equal(2, historicalLogIds.Length);
        Assert.True(historicalLogIds[0] > 0 && historicalLogIds[1] > historicalLogIds[0]);
        Assert.Equal(new[] { "historical one", "historical two" }, await conn.QueryAsync<string>("SELECT logs FROM builds_logs ORDER BY id"));
        var newLogId = await conn.ExecuteScalarAsync<long>("INSERT INTO builds_logs(plugin_slug, build_id, logs) VALUES ('old', 0, 'new log') RETURNING id");
        Assert.True(newLogId > historicalLogIds[1]);
        await conn.ExecuteAsync("INSERT INTO builds(plugin_slug, id, state, triggered_by) VALUES ('old', 1, 'queued', 'existing')");
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_events WHERE type = 'user.first_build_triggered'"));

        await conn.ExecuteAsync(InsertUser, new { id = "new-builder" });
        await using var other = await tester.Open();
        const string build = "INSERT INTO builds(plugin_slug, id, state, triggered_by) VALUES (@slug, 0, 'queued', 'new-builder')";
        await Task.WhenAll(conn.ExecuteAsync(build, new { slug = "one" }), other.ExecuteAsync(build, new { slug = "two" }));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_events WHERE type = 'user.first_build_triggered'"));
        Assert.Equal(3, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM admin_events WHERE type = 'build.triggered'"));
    }

    private sealed class TestEmailService() : EmailService(null!, null!, NullLogger<EmailService>.Instance)
    {
        public bool Fail { get; set; } = true;
        public bool Block { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<List<string>> DeliverEmail(IEnumerable<InternetAddress> toList, string subject, string messageText, CancellationToken cancellationToken = default)
        {
            if (Block)
            {
                Started.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            if (Fail) throw new InvalidOperationException("Simulated SMTP failure");
            return ["admin@example.com"];
        }
    }
}

public class AdminEventApiTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    [Fact]
    public async Task AdminCanPollAndManageSubscriptionsButOrdinaryUsersCannot()
    {
        await using var tester = Create();
        tester.ReuseDatabase = false;
        tester.ConfigureServices = services =>
        {
            // Exercise real routing, Identity and Basic auth without Docker/storage
            // startup or a background worker racing the delivery assertions.
            foreach (var descriptor in services.Where(s => s.ServiceType == typeof(IHostedService) &&
                         s.ImplementationType != typeof(DatabaseStartupHostedService)).ToArray())
                services.Remove(descriptor);
        };
        await tester.Start();
        using var scope = tester.WebApp.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var admin = new IdentityUser { UserName = "event-admin@example.com", Email = "event-admin@example.com", EmailConfirmed = true };
        var ordinary = new IdentityUser { UserName = "event-user@example.com", Email = "event-user@example.com", EmailConfirmed = true };
        Assert.True((await users.CreateAsync(admin, "test-password-123")).Succeeded);
        Assert.True((await users.AddToRoleAsync(admin, Roles.ServerAdmin)).Succeeded);
        Assert.True((await users.CreateAsync(ordinary, "test-password-123")).Succeeded);
        using var client = tester.CreateHttpClient();
        const string path = "/api/v1/admin/events";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        client.SetBasicAuth(ordinary.UserName, "test-password-123");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(path + "/subscriptions",
            new { kind = "email", destination = "admin@example.com", eventTypes = new[] { "user.registered" } })).StatusCode);

        client.SetBasicAuth(admin.UserName, "test-password-123");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(path + "?limit=101")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(path + "?after=-1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(path + "?type=unknown")).StatusCode);
        var page = JObject.Parse(await client.GetStringAsync(path));
        Assert.Equal(2, ((JArray)page["events"]!).Count);
        Assert.Equal("2", page["nextCursor"]!.Value<string>());

        var create = await client.PostAsJsonAsync(path + "/subscriptions", new
        {
            kind = "webhook", destination = "https://example.com/hooks", eventTypes = new[] { "user.registered" }
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = JObject.Parse(await create.Content.ReadAsStringAsync());
        var id = created["id"]!.Value<string>();
        var secret = created["secret"]!.Value<string>()!;
        Assert.Equal(32, Convert.FromBase64String(secret).Length);
        var listed = await client.GetStringAsync(path + "/subscriptions");
        Assert.DoesNotContain(secret, listed);
        Assert.DoesNotContain("protectedSecret", listed, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path + "/subscriptions", new
        {
            kind = "webhook", destination = "https://169.254.169.254/latest/meta-data", eventTypes = Array.Empty<string>()
        })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path + "/subscriptions", new
        {
            kind = "email", destination = "admin@example.com", eventTypes = new[] { "unknown" }
        })).StatusCode);

        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.ExecuteAsync("SELECT emit_admin_event('user.registered', '{}')");
        var deliveries = JArray.Parse(await client.GetStringAsync(path + $"/subscriptions/{id}/deliveries"));
        Assert.Single(deliveries);
        Assert.Equal("pending", deliveries[0]["status"]!.Value<string>());
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(path + $"/subscriptions/{id}/enabled", new { enabled = false })).StatusCode);
        await conn.ExecuteAsync("SELECT emit_admin_event('user.registered', '{}')");
        Assert.Single(JArray.Parse(await client.GetStringAsync(path + $"/subscriptions/{id}/deliveries")));
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(path + $"/subscriptions/{id}")).StatusCode);
        Assert.Equal(4, ((JArray)JObject.Parse(await client.GetStringAsync(path))["events"]!).Count);

        await users.RemoveFromRoleAsync(admin, Roles.ServerAdmin);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
    }
}
