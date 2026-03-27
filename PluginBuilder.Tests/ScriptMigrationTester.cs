using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace PluginBuilder.Tests;

public sealed class ScriptMigrationTester : IAsyncDisposable
{
    private readonly ILogger<ScriptMigrationTester> _logger;
    private readonly string _dbName;
    private readonly string _connectionString;
    private readonly string _serverConnectionString;
    private ScriptResource[]? _pendingScripts;

    public ScriptMigrationTester(string testFolder, XUnitLogger logs)
    {
        _logger = logs.CreateLogger<ScriptMigrationTester>();
        _dbName = $"{testFolder.ToLowerInvariant()}_{Guid.NewGuid():N}".ToLowerInvariant();
        _connectionString = $"User ID=postgres;Include Error Detail=true;Host=127.0.0.1;Port=61932;Database={_dbName}";
        var csb = new NpgsqlConnectionStringBuilder(_connectionString) { Database = "postgres" };
        _serverConnectionString = csb.ToString();
    }

    public async Task RunScriptsUntil(string firstPendingScript)
    {
        await EnsureCreatedAsync();

        var scripts = GetScripts();
        var firstPendingIndex = Array.FindIndex(scripts, s => s.ScriptName == firstPendingScript);
        if (firstPendingIndex == -1)
            throw new InvalidOperationException($"Script {firstPendingScript} not found");

        _pendingScripts = scripts[firstPendingIndex..];

        await using var conn = await Open();
        await ExecuteScripts(conn, scripts[..firstPendingIndex]);
    }

    public async Task RunRemainingScripts()
    {
        if (_pendingScripts is null)
            throw new InvalidOperationException("Call RunScriptsUntil first");

        await using var conn = await Open();
        await ExecuteScripts(conn, _pendingScripts);
        _pendingScripts = null;
    }

    public async Task<NpgsqlConnection> Open()
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        return conn;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var targetConn = new NpgsqlConnection(_connectionString);
            NpgsqlConnection.ClearPool(targetConn);

            await using var conn = new NpgsqlConnection(_serverConnectionString);
            await conn.OpenAsync();

            await using (var terminate = new NpgsqlCommand(
                             """
                             SELECT pg_terminate_backend(pid)
                             FROM pg_stat_activity
                             WHERE datname = @dbName AND pid <> pg_backend_pid();
                             """,
                             conn))
            {
                terminate.Parameters.AddWithValue("dbName", _dbName);
                await terminate.ExecuteNonQueryAsync();
            }

            var safeDb = _dbName.Replace("\"", "\"\"");
            await using var drop = new NpgsqlCommand($"""DROP DATABASE IF EXISTS "{safeDb}" """, conn);
            await drop.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not drop migration test database {dbName}", _dbName);
        }
    }

    private async Task EnsureCreatedAsync()
    {
        await using var conn = new NpgsqlConnection(_serverConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync($"""CREATE DATABASE "{_dbName}" TEMPLATE 'template0' LC_CTYPE 'C' LC_COLLATE 'C' ENCODING 'UTF8'""");
        _logger.LogInformation("DB: {dbName}", _dbName);
    }

    private static ScriptResource[] GetScripts()
    {
        return typeof(Program).Assembly
            .GetManifestResourceNames()
            .Where(n => n.EndsWith(".sql", StringComparison.InvariantCulture))
            .Select(resourceName =>
            {
                var parts = resourceName.Split('.');
                if (!int.TryParse(parts[^3], NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                    return null;
                return new ScriptResource(resourceName, $"{parts[^3]}.{parts[^2]}");
            })
            .Where(r => r is not null)
            .OrderBy(r => r!.ResourceName)
            .Select(r => r!)
            .ToArray();
    }

    private static async Task ExecuteScripts(NpgsqlConnection conn, ScriptResource[] scripts)
    {
        foreach (var script in scripts)
        {
            await using var stream = typeof(Program).Assembly
                .GetManifestResourceStream(script.ResourceName)
                ?? throw new InvalidOperationException($"Embedded script {script.ResourceName} not found");
            using StreamReader reader = new(stream, Encoding.UTF8);
            var content = await reader.ReadToEndAsync();
            await conn.ExecuteAsync($"{content}; INSERT INTO migrations VALUES (@scriptName)", new { scriptName = script.ScriptName });
        }
    }

    private sealed record ScriptResource(string ResourceName, string ScriptName);
}
