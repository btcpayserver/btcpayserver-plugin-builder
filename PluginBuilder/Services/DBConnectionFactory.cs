using Npgsql;

namespace PluginBuilder.Services
{
    public class DBConnectionFactory
    {
        public NpgsqlConnectionStringBuilder ConnectionString { get; }
        public DBConnectionFactory(IConfiguration config)
        {
            try
            {
                ConnectionString = new NpgsqlConnectionStringBuilder(config.GetRequired("POSTGRES"));
            }
            catch (Exception ex) when (ex is not ConfigurationException)
            {
                throw new ConfigurationException("POSTGRES", ex.Message);
            }
        }

        public async Task<NpgsqlConnection> Open(CancellationToken cancellationToken = default)
        {
            int maxRetries = 10;
            int retries = maxRetries;
retry:
            var conn = new Npgsql.NpgsqlConnection(ConnectionString.ToString());
            try
            {
                await conn.OpenAsync(cancellationToken);
            }
            catch (PostgresException ex) when (ex.IsTransient && retries > 0)
            {
                retries--;
                await conn.DisposeAsync();
                await Task.Delay((maxRetries - retries) * 100, cancellationToken);
                goto retry;
            }
            catch
            {
                conn.Dispose();
                throw;
            }
            return conn;
        }

    }
}
