using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Dapper;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;

namespace PluginBuilder.HostedServices;

/// <summary>
/// Receives the front proxy's trimmed access log over syslog (UDP) on ACCESSLOG_SYSLOG_PORT and keeps it for
/// ACCESSLOG_RETENTION_DAYS. Without a port nothing is received, but old rows are still purged. The port must only be
/// reachable from the proxy's network: syslog has no authentication.
/// </summary>
public class AccessLogHostedService : BackgroundService
{
    public const int BatchSize = 500;
    private readonly DBConnectionFactory _connections;
    private readonly ILogger<AccessLogHostedService> _logger;
    private readonly int? _port;
    private readonly Channel<AccessLogEntry> _queue = Channel.CreateBounded<AccessLogEntry>(
        new BoundedChannelOptions(50_000) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = true });
    private long _refused, _dropped;
    private readonly TaskCompletionSource<int> _bound = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AccessLogHostedService(DBConnectionFactory connections, IConfiguration configuration, ILogger<AccessLogHostedService> logger)
    {
        _connections = connections;
        _logger = logger;
        Days = configuration.GetValue("ACCESSLOG_RETENTION_DAYS", 7);
        if (Days is < 1 or > 90)
            throw new ConfigurationException("ACCESSLOG_RETENTION_DAYS", "Must be between 1 and 90 days.");
        var port = configuration["ACCESSLOG_SYSLOG_PORT"];
        if (string.IsNullOrWhiteSpace(port))
            return;
        if (!int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value > IPEndPoint.MaxPort)
            throw new ConfigurationException("ACCESSLOG_SYSLOG_PORT", "Must be a UDP port number.");
        _port = value;
    }

    public int Days { get; }

    /// <summary>The UDP port the receiver listens on, once bound (port 0 picks a free one).</summary>
    public Task<int> Bound => _bound.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var loops = new List<Task> { PurgeLoop(stoppingToken) };
        UdpClient? udp = null;
        if (_port is { } port)
            try
            {
                udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
                _bound.TrySetResult(((IPEndPoint)udp.Client.LocalEndPoint!).Port);
                loops.Add(ReceiveLoop(udp, stoppingToken));
                loops.Add(WriteLoop(stoppingToken));
            }
            catch (SocketException ex)
            {
                // The site keeps serving without the access log rather than failing to start.
                _logger.LogError(ex, "Access log receiver could not listen on UDP port {Port}", port);
                _bound.TrySetException(ex);
            }
        else
            _bound.TrySetCanceled(CancellationToken.None);

        using (udp)
            await Task.WhenAll(loops);
    }

    private async Task ReceiveLoop(UdpClient udp, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult datagram;
            try
            {
                datagram = await udp.ReceiveAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (SocketException) { continue; }

            if (!AccessLogLine.TryParse(datagram.Buffer, out var entry))
                Interlocked.Increment(ref _refused);
            else if (!_queue.Writer.TryWrite(entry!))
                Interlocked.Increment(ref _dropped);
        }
        _queue.Writer.TryComplete();
    }

    private async Task WriteLoop(CancellationToken cancellationToken)
    {
        var batch = new List<AccessLogEntry>(BatchSize);
        try
        {
            while (await _queue.Reader.WaitToReadAsync(cancellationToken))
            {
                while (batch.Count < BatchSize && _queue.Reader.TryRead(out var entry))
                    batch.Add(entry);
                try
                {
                    await Insert(batch, cancellationToken);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    Interlocked.Add(ref _dropped, batch.Count);
                    _logger.LogWarning(ex, "Access log: could not store {Count} entries", batch.Count);
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }
                batch.Clear();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task Insert(List<AccessLogEntry> batch, CancellationToken cancellationToken)
    {
        await using var conn = await _connections.Open(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO nginx_access_log (logged_at, ip, host, method, path, status, bytes, user_agent)
            SELECT logged_at::timestamptz, ip::inet, host, method, path, status, bytes, user_agent
            FROM unnest(@loggedAt::text[], @ip::text[], @host::text[], @method::text[], @path::text[], @status::smallint[], @bytes::bigint[],
                @userAgent::text[]) AS batch(logged_at, ip, host, method, path, status, bytes, user_agent)
            """, new
        {
            loggedAt = batch.Select(e => e.LoggedAt.ToString("O", CultureInfo.InvariantCulture)).ToArray(),
            ip = batch.Select(e => e.Ip.ToString()).ToArray(),
            host = batch.Select(e => e.Host).ToArray(),
            method = batch.Select(e => e.Method).ToArray(),
            path = batch.Select(e => e.Path).ToArray(),
            status = batch.Select(e => (short)e.Status).ToArray(),
            bytes = batch.Select(e => e.Bytes).ToArray(),
            userAgent = batch.Select(e => e.UserAgent).ToArray()
        }, cancellationToken: cancellationToken));
    }

    public async Task<int> Purge(CancellationToken cancellationToken)
    {
        await using var conn = await _connections.Open(cancellationToken);
        return await conn.ExecuteAsync(new CommandDefinition("""
            DELETE FROM nginx_access_log WHERE id IN (
                SELECT id FROM nginx_access_log WHERE logged_at < @cutoff ORDER BY logged_at LIMIT 10000)
            """, new { cutoff = DateTimeOffset.UtcNow.AddDays(-Days) }, cancellationToken: cancellationToken));
    }

    private async Task PurgeLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                while (await Purge(cancellationToken) > 0)
                    await Task.Delay(100, cancellationToken);
                var refused = Interlocked.Exchange(ref _refused, 0);
                var dropped = Interlocked.Exchange(ref _dropped, 0);
                if (refused + dropped > 0)
                    _logger.LogWarning("Access log: {Refused} datagrams refused as malformed and {Dropped} entries dropped in the last hour",
                        refused, dropped);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Access log retention cleanup failed; will retry"); }
            try { await Task.Delay(TimeSpan.FromHours(1), cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }
}
