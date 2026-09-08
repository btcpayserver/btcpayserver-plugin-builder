using Npgsql;
using PluginBuilder.Util.Extensions;

namespace PluginBuilder.Services;

public class DBConnectionFactory : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    public DBConnectionFactory(IConfiguration config)
    {
        try
        {
            ConnectionString = new NpgsqlConnectionStringBuilder(config.GetRequired("POSTGRES"));
            _dataSource = NpgsqlDataSource.Create(ConnectionString.ToString());
        }
        catch (Exception ex) when (ex is not ConfigurationException)
        {
            throw new ConfigurationException("POSTGRES", ex.Message);
        }
    }

    public NpgsqlConnectionStringBuilder ConnectionString { get; }

    public async Task<NpgsqlConnection> Open(CancellationToken cancellationToken = default)
    {
        var maxRetries = 10;
        var retries = maxRetries;
        retry:
        NpgsqlConnection conn = _dataSource.CreateConnection();
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

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
