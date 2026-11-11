using Dapper;
using Npgsql;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests;

public class PluginTitleUniquenessTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    [Fact]
    public async Task MigrationNormalizesTitlesAndUsesTheSameKeyAsValidation()
    {
        await using var tester = CreateMigrationTester("title-normalization");
        await tester.RunScriptsUntil("35.PluginTitleUniqueness");
        await using (var conn = await tester.Open())
        {
            await conn.ExecuteAsync("""
                INSERT INTO plugins (slug, settings) VALUES
                ('cafe', jsonb_build_object('pluginTitle', @Title::text, 'description', 'Keep this')),
                ('untitled', '{}'), ('null-title', '{"pluginTitle":null}');
                INSERT INTO builds(plugin_slug, id, state, manifest_info)
                VALUES ('untitled', 0, 'uploaded', '{"Name":"Legacy manifest title","Version":"1.0.0"}');
                INSERT INTO versions(plugin_slug, ver, build_id, btcpay_min_ver, pre_release)
                VALUES ('untitled', ARRAY[1,0,0], 0, ARRAY[0,0,0], true);
                """, new { Title = "  Café  Payments  " });
        }

        await tester.RunRemainingScripts();
        await using var migrated = await tester.Open();
        Assert.Equal("Café  Payments", await migrated.QuerySingleAsync<string>(
            "SELECT settings->>'pluginTitle' FROM plugins WHERE slug='cafe'"));
        Assert.Equal("Keep this", await migrated.QuerySingleAsync<string>(
            "SELECT settings->>'description' FROM plugins WHERE slug='cafe'"));
        Assert.True(await migrated.IsPluginTitleInUse("  CAFÉ  PAYMENTS\t"));
        Assert.False(await migrated.IsPluginTitleInUse("Cafe  Payments"));
        Assert.False(await migrated.IsPluginTitleInUse("Café Payments"));
        Assert.False(await migrated.IsPluginTitleInUse("Café  Payments", PluginSlug.Parse("cafe")));
        Assert.False(await migrated.IsPluginTitleInUse("Legacy manifest title"));
        Assert.Null((await migrated.GetSettings("untitled"))?.PluginTitle);
        Assert.True(await migrated.SetPluginSettings("untitled", null));
        Assert.Equal("Legacy manifest title", await migrated.QuerySingleAsync<string>(
            "SELECT manifest_info->>'Name' FROM builds WHERE plugin_slug='untitled'"));

        Assert.True(await migrated.SetPluginSettings("cafe", new PluginSettings { PluginTitle = "  CAFé  Payments " }));
        Assert.Equal("CAFé  Payments", (await migrated.GetSettings("cafe"))!.PluginTitle);
        var duplicate = await Assert.ThrowsAsync<PluginTitleValidationException>(() =>
            migrated.SetPluginSettings("untitled", new PluginSettings { PluginTitle = "\nCafé  Payments " }));
        Assert.Equal(PluginTitleValidationException.DuplicateMessage, duplicate.Message);
        Assert.Equal(PluginTitleValidationException.UniqueConstraint, Assert.IsType<PostgresException>(duplicate.InnerException).ConstraintName);
        Assert.Null((await migrated.GetSettings("untitled"))?.PluginTitle);
        var tooLong = await Assert.ThrowsAsync<PluginTitleValidationException>(() =>
            migrated.SetPluginSettings("untitled", new PluginSettings { PluginTitle = new string('x', 501) }));
        Assert.Equal(PluginTitleValidationException.LengthMessage, tooLong.Message);
        Assert.Equal(PluginTitleValidationException.LengthConstraint, Assert.IsType<PostgresException>(tooLong.InnerException).ConstraintName);
        Assert.Null((await migrated.GetSettings("untitled"))?.PluginTitle);

        var directWrite = await Assert.ThrowsAsync<PostgresException>(() => migrated.ExecuteAsync("""
            UPDATE plugins SET settings = '{"pluginTitle":"  café  Payments  "}' WHERE slug='null-title';
            """));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, directWrite.SqlState);
        Assert.Equal(PluginTitleValidationException.UniqueConstraint, directWrite.ConstraintName);
    }

    [Fact]
    public async Task MigrationRejectsCollisionsWithoutChangingExistingTitles()
    {
        await using var tester = CreateMigrationTester("title-collisions");
        await tester.RunScriptsUntil("35.PluginTitleUniqueness");
        await using (var conn = await tester.Open())
        {
            await conn.ExecuteAsync("""
                INSERT INTO plugins (slug, settings) VALUES
                ('first', '{"pluginTitle":"  Shared title "}'),
                ('second', '{"pluginTitle":"SHARED TITLE"}');
                """);
        }

        var error = await Assert.ThrowsAsync<PostgresException>(() => tester.RunRemainingScripts());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        Assert.Equal(PluginTitleValidationException.UniqueConstraint, error.ConstraintName);
        await using var verify = await tester.Open();
        Assert.Equal("  Shared title ", await verify.QuerySingleAsync<string>(
            "SELECT settings->>'pluginTitle' FROM plugins WHERE slug='first'"));
        Assert.Equal("SHARED TITLE", (await verify.GetSettings("second"))!.PluginTitle);
        Assert.False(await verify.QuerySingleAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM migrations WHERE script_name='35.PluginTitleUniqueness')"));
        Assert.Null(await verify.QuerySingleAsync<string?>("SELECT to_regclass('plugins_title_unique')::text"));

        // Only an explicit maintainer decision resolves a collision; retrying then succeeds.
        await verify.ExecuteAsync("UPDATE plugins SET settings='{}' WHERE slug='second'");
        await tester.RunRemainingScripts();
        Assert.Equal("Shared title", (await verify.GetSettings("first"))!.PluginTitle);
    }

    [Fact]
    public async Task ConcurrentWritesRejectOneTitleAndPreserveTheLosingPlugin()
    {
        await using var tester = CreateMigrationTester("title-race");
        await tester.RunScriptsUntil("35.PluginTitleUniqueness");
        await tester.RunRemainingScripts();
        await using var first = await tester.Open();
        await using var second = await tester.Open();
        await first.ExecuteAsync("""
            INSERT INTO plugins (slug, settings) VALUES
            ('winner', '{}'), ('loser', '{"pluginTitle":"Original title"}');
            """);
        await using var firstTransaction = await first.BeginTransactionAsync();
        await first.SetPluginSettings("winner", new PluginSettings { PluginTitle = "  Shared title  " });
        await using var secondTransaction = await second.BeginTransactionAsync();
        var pending = second.SetPluginSettings("loser",
            new PluginSettings { PluginTitle = "\tSHARED TITLE\n" });

        // Observe the second PostgreSQL backend waiting on the first transaction's UNIQUE check.
        await using var observer = await tester.Open();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await observer.QuerySingleAsync<bool>(
                   "SELECT cardinality(pg_blocking_pids(@Pid)) > 0", new { Pid = second.ProcessID }))
            await Task.Delay(10, timeout.Token);
        Assert.False(pending.IsCompleted);
        await firstTransaction.CommitAsync();
        await Assert.ThrowsAsync<PluginTitleValidationException>(() => pending);
        await secondTransaction.RollbackAsync();

        Assert.Equal(1, await observer.QuerySingleAsync<int>(
            """
            SELECT count(*) FROM plugins
            WHERE lower(nullif(btrim(settings->>'pluginTitle'), '') COLLATE "pg_c_utf8") =
                  lower('shared title' COLLATE "pg_c_utf8")
            """));
        Assert.Equal("Original title", (await observer.GetSettings("loser"))!.PluginTitle);
    }

    [Fact]
    public async Task MigrationRejectsOversizedTitlesWithoutTruncatingThem()
    {
        await using var tester = CreateMigrationTester("title-too-long");
        await tester.RunScriptsUntil("35.PluginTitleUniqueness");
        var title = new string('é', 501);
        await using var conn = await tester.Open();
        await conn.ExecuteAsync("INSERT INTO plugins(slug, settings) VALUES('too-long', jsonb_build_object('pluginTitle', @Title::text))",
            new { Title = title });
        var error = await Assert.ThrowsAsync<PostgresException>(() => tester.RunRemainingScripts());
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal(PluginTitleValidationException.LengthConstraint, error.ConstraintName);
        Assert.Equal(title, (await conn.GetSettings("too-long"))!.PluginTitle);
        await conn.ExecuteAsync("UPDATE plugins SET settings=jsonb_build_object('pluginTitle', @Title::text) WHERE slug='too-long'",
            new { Title = new string('界', 500) });
        await tester.RunRemainingScripts();
        Assert.Equal(new string('界', 500), (await conn.GetSettings("too-long"))!.PluginTitle);
    }

    [Fact]
    public async Task MigrationAndItsMarkerRollBackTogetherWhenRecordingFails()
    {
        await using var tester = CreateMigrationTester("title-marker");
        await tester.RunScriptsUntil("35.PluginTitleUniqueness");
        await using var conn = await tester.Open();
        await conn.ExecuteAsync("""
            INSERT INTO plugins(slug, settings) VALUES('unchanged', '{"pluginTitle":"  Original title  "}');
            INSERT INTO migrations VALUES('35.PluginTitleUniqueness');
            """);
        var error = await Assert.ThrowsAsync<PostgresException>(() => tester.RunRemainingScripts());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        Assert.Equal("  Original title  ", (await conn.GetSettings("unchanged"))!.PluginTitle);
        Assert.Null(await conn.QuerySingleAsync<string?>("SELECT to_regclass('plugins_title_unique')::text"));
        await conn.ExecuteAsync("DELETE FROM migrations WHERE script_name='35.PluginTitleUniqueness'");
        await tester.RunRemainingScripts();
        Assert.Equal("Original title", (await conn.GetSettings("unchanged"))!.PluginTitle);
    }
}
