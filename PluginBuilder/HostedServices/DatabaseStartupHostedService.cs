using System.Globalization;
using System.Text;
using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PluginBuilder.Services;

namespace PluginBuilder.HostedServices
{
    public class DatabaseStartupHostedService : IHostedService
    {
        ILogger Logger { get; }
        public DatabaseStartupHostedService(ILogger<DatabaseStartupHostedService> logger, DBConnectionFactory connectionFactory)
        {
            ConnectionFactory = connectionFactory;
            Logger = logger;
        }

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
            catch (Npgsql.NpgsqlException pgex) when (pgex.SqlState == "3D000")
            {
                var builder = new Npgsql.NpgsqlConnectionStringBuilder(ConnectionFactory.ConnectionString.ToString());
                var dbname = builder.Database;
                Logger.LogInformation($"Database '{dbname}' doesn't exists, creating it...");
                builder.Database = null;
                var conn2Str = builder.ToString();
                await using (var conn2 = new Npgsql.NpgsqlConnection(conn2Str))
                {
                    await conn2.OpenAsync(cancellationToken);
                    await conn2.ExecuteAsync($"CREATE DATABASE {dbname} TEMPLATE 'template0' LC_CTYPE 'C' LC_COLLATE 'C' ENCODING 'UTF8'");
                }
                goto retry;
            }
        }

        private async Task CleanupScript(Npgsql.NpgsqlConnection conn)
        {
            await conn.ExecuteAsync("UPDATE builds SET state = 'failed' WHERE state = 'running' OR state = 'scheduled' OR state = 'uploading'");
            
        }

        private async Task RunScripts(Npgsql.NpgsqlConnection conn)
        {
                HashSet<string> executed;
                try
                {
                    executed = (await conn.QueryAsync<string>("SELECT script_name FROM migrations")).ToHashSet();
                }
                catch (Npgsql.NpgsqlException ex) when (ex.SqlState == "42P01")
                {
                    executed = new HashSet<string>();
                }
                foreach (var resource in System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceNames()
                                                                 .Where(n => n.EndsWith(".sql", System.StringComparison.InvariantCulture))
                                                                 .OrderBy(n => n))
                {
                    var parts = resource.Split('.');
                    if (!int.TryParse(parts[^3], NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                        continue;
                    var scriptName = $"{parts[^3]}.{parts[^2]}";
                    if (executed.Contains(scriptName))
                        continue;
                    var stream = System.Reflection.Assembly.GetExecutingAssembly()
                                                           .GetManifestResourceStream(resource)!;
                    string content;
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        content = await reader.ReadToEndAsync();
                    }
                    Logger.LogInformation($"Execute script {scriptName}...");
                    await conn.ExecuteAsync($"{content}; INSERT INTO migrations VALUES (@scriptName)", new { scriptName });
                }
            
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
