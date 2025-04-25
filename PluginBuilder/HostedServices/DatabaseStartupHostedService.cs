using System.Globalization;
using System.Reflection;
using System.Text;
using Dapper;
using Npgsql;
using PluginBuilder.Services;

namespace PluginBuilder.HostedServices;

public class DatabaseStartupHostedService : IHostedService
{
    public DatabaseStartupHostedService(ILogger<DatabaseStartupHostedService> logger, DBConnectionFactory connectionFactory)
    {
        ConnectionFactory = connectionFactory;
        Logger = logger;
    }

    private ILogger Logger { get; }

    public DBConnectionFactory ConnectionFactory { get; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        retry:
        try
        {
            await using var conn = await ConnectionFactory.Open(cancellationToken);
            await RunScripts(conn);
            await CleanupScript(conn);
        }
        catch (NpgsqlException pgex) when (pgex.SqlState == "3D000")
        {
            NpgsqlConnectionStringBuilder builder = new(ConnectionFactory.ConnectionString.ToString());
            var dbname = builder.Database;
            Logger.LogInformation($"Database '{dbname}' doesn't exists, creating it...");
            builder.Database = null;
            var conn2Str = builder.ToString();
            await using (NpgsqlConnection conn2 = new(conn2Str))
            {
                await conn2.OpenAsync(cancellationToken);
                await conn2.ExecuteAsync($"CREATE DATABASE {dbname} TEMPLATE 'template0' LC_CTYPE 'C' LC_COLLATE 'C' ENCODING 'UTF8'");
            }

            goto retry;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private async Task CleanupScript(NpgsqlConnection conn)
    {
        await conn.ExecuteAsync(
            "UPDATE builds SET state = 'failed', build_info = '{\"error\": \"Interrupted because the server restarted\"}'::JSONB WHERE state IN ('running', 'queued', 'uploading');");
    }

    private async Task RunScripts(NpgsqlConnection conn)
    {
        HashSet<string> executed;
        try
        {
            executed = (await conn.QueryAsync<string>("SELECT script_name FROM migrations")).ToHashSet();
        }
        catch (NpgsqlException ex) when (ex.SqlState == "42P01")
        {
            executed = new HashSet<string>();
        }

        foreach (var resource in Assembly.GetExecutingAssembly().GetManifestResourceNames()
                     .Where(n => n.EndsWith(".sql", StringComparison.InvariantCulture))
                     .OrderBy(n => n))
        {
            var parts = resource.Split('.');
            if (!int.TryParse(parts[^3], NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                continue;
            var scriptName = $"{parts[^3]}.{parts[^2]}";
            if (executed.Contains(scriptName))
                continue;
            var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(resource)!;
            string content;
            using (StreamReader reader = new(stream, Encoding.UTF8))
            {
                content = await reader.ReadToEndAsync();
            }

            Logger.LogInformation($"Execute script {scriptName}...");
            await conn.ExecuteAsync($"{content}; INSERT INTO migrations VALUES (@scriptName)", new { scriptName });
        }
    }
}
