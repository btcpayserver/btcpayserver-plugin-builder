using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PluginBuilder.Services;
using PluginBuilder.Util;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests;

public class BuildOutputCaptureTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1025)]
    public async Task PersistsOrderedLinesInBoundedStatementsAndFlushesOnDisposal(int lineCount)
    {
        await using var tester = CreateMigrationTester();
        await tester.RunScriptsUntil("25.AdminEvents");
        await tester.RunRemainingScripts();
        await using var connection = await tester.Open();
        await using var factory = new DBConnectionFactory(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["POSTGRES"] = connection.ConnectionString })
            .Build());

        // Count actual INSERT statements and the rows in each, rather than Dapper calls.
        await connection.ExecuteAsync("""
            INSERT INTO plugins(slug) VALUES ('batch-logs');
            INSERT INTO builds(plugin_slug, id, state) VALUES ('batch-logs', 1, 'running');
            CREATE TABLE test_log_batches (line_count integer NOT NULL);
            CREATE FUNCTION record_test_log_batch() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                INSERT INTO test_log_batches SELECT count(*) FROM inserted_lines;
                RETURN NULL;
            END $$;
            CREATE TRIGGER record_test_log_batch AFTER INSERT ON builds_logs
                REFERENCING NEW TABLE AS inserted_lines
                FOR EACH STATEMENT EXECUTE FUNCTION record_test_log_batch();
            """);

        var input = Enumerable.Range(0, lineCount)
            .Select(i => i == 0 ? "" : $"{i}: before\0after\tλ🙂 'quoted'")
            .ToArray();
        await using (var capture = new BuildService.BuildOutputCapture(
                         new FullBuildId(new PluginSlug("batch-logs"), 1), factory,
                         NullLogger<BuildService>.Instance))
        {
            foreach (var line in input)
                capture.AddLine(line);
        }

        var persisted = await connection.QueryAsync<string>("""
            SELECT logs FROM builds_logs WHERE plugin_slug = 'batch-logs' AND build_id = 1 ORDER BY id
            """);
        Assert.Equal(input.Select(line => line.Replace("\0", string.Empty)), persisted);
        var batchSizes = (await connection.QueryAsync<int>("SELECT line_count FROM test_log_batches")).ToArray();
        Assert.Equal(lineCount, batchSizes.Sum());
        Assert.All(batchSizes, size => Assert.InRange(size, 1, 256));
        if (lineCount == 0)
            Assert.Empty(batchSizes);
        if (lineCount > 256)
        {
            Assert.True(batchSizes.Length > 1);
            Assert.Contains(batchSizes, size => size > 1);
            Assert.True(batchSizes.Length < lineCount);
        }
    }

    [Fact]
    public async Task PersistsLiveOutputBeforeDisposalAndFlushesTrailingLine()
    {
        await using var tester = CreateMigrationTester();
        await tester.RunScriptsUntil("25.AdminEvents");
        await tester.RunRemainingScripts();
        await using var connection = await tester.Open();
        await using var factory = new DBConnectionFactory(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["POSTGRES"] = connection.ConnectionString })
            .Build());
        await connection.ExecuteAsync("""
            INSERT INTO plugins(slug) VALUES ('live-logs');
            INSERT INTO builds(plugin_slug, id, state) VALUES ('live-logs', 1, 'running');
            """);

        await using (var capture = new BuildService.BuildOutputCapture(
                         new FullBuildId(new PluginSlug("live-logs"), 1), factory,
                         NullLogger<BuildService>.Instance))
        {
            capture.AddLine("live line");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM builds_logs") == 0)
                await Task.Delay(10, timeout.Token);
            capture.AddLine("trailing line");
        }

        Assert.Equal(new[] { "live line", "trailing line" },
            await connection.QueryAsync<string>("SELECT logs FROM builds_logs ORDER BY id"));
    }
}
