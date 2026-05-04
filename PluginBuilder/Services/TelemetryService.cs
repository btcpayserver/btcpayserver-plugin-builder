using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using PluginBuilder.DataModels;

namespace PluginBuilder.Services;

public class TelemetryService(DBConnectionFactory connectionFactory, ILogger<TelemetryService> logger)
{
    private static readonly Regex BTCPayUserAgentRegex = new(@"^BTCPayServer/(\d+\.\d+\.\d+[\w.\-]*)", RegexOptions.Compiled);

    public async Task RecordPluginDownload(string pluginSlug, string version, string? userAgent, string? remoteIp)
    {
        try
        {
            if (!TryParseBTCPayUserAgent(userAgent, out var btcpayVersion))
                return;

            if (!TryGetPublicIp(remoteIp, out var ip))
                return;

            var hashedIp = HashIp(ip!);
            var now = DateTimeOffset.UtcNow;

            await using var conn = await connectionFactory.Open();
            await conn.ExecuteAsync("""
                INSERT INTO plugin_downloads (plugin_slug, version, timestamp, hashed_ip, btcpay_version)
                VALUES (@PluginSlug, @Version, @Timestamp, @HashedIp, @BTCPayVersion)
                """,
                new { PluginSlug = pluginSlug, Version = version, Timestamp = now, HashedIp = hashedIp, BTCPayVersion = btcpayVersion });

            var existing = await conn.QueryFirstOrDefaultAsync<PluginServerInstall>("""
                SELECT * FROM plugin_server_installs
                WHERE hashed_ip = @HashedIp AND plugin_slug = @PluginSlug
                """,
                new { HashedIp = hashedIp, PluginSlug = pluginSlug });

            if (existing is null)
            {
                await conn.ExecuteAsync("""
                    INSERT INTO plugin_server_installs
                        (hashed_ip, plugin_slug, version, btcpay_version, installed_at, updated_at, uninstalled_at, install_count)
                    VALUES
                        (@HashedIp, @PluginSlug, @Version, @BTCPayVersion, @Now, @Now, NULL, 1)
                    """,
                    new { HashedIp = hashedIp, PluginSlug = pluginSlug, Version = version, BTCPayVersion = btcpayVersion, Now = now });
            }
            else if (existing.UninstalledAt != null)
            {
                await conn.ExecuteAsync("""
                    UPDATE plugin_server_installs
                    SET version = @Version,
                        btcpay_version = @BTCPayVersion,
                        installed_at = @Now,
                        updated_at = @Now,
                        uninstalled_at = NULL,
                        install_count = install_count + 1
                    WHERE hashed_ip = @HashedIp AND plugin_slug = @PluginSlug
                    """,
                    new { HashedIp = hashedIp, PluginSlug = pluginSlug, Version = version, BTCPayVersion = btcpayVersion, Now = now });
            }
            else
            {
                await conn.ExecuteAsync("""
                    UPDATE plugin_server_installs
                    SET version = @Version, btcpay_version = @BTCPayVersion, updated_at = @Now
                    WHERE hashed_ip = @HashedIp AND plugin_slug = @PluginSlug
                    """,
                    new { HashedIp = hashedIp, PluginSlug = pluginSlug, Version = version, BTCPayVersion = btcpayVersion, Now = now });
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record download telemetry for {PluginSlug} {Version}", pluginSlug, version);
        }
    }

    public async Task RecordServerSnapshot(string? remoteIp, string btcpayVersion, IEnumerable<PluginReport> plugins)
    {
        try
        {
            if (!TryGetPublicIp(remoteIp, out var ip))
                return;

            var hashedIp = HashIp(ip!);
            var now = DateTimeOffset.UtcNow;
            var pluginList = plugins.ToList();

            await using var conn = await connectionFactory.Open();

            var existing = (await conn.QueryAsync<PluginServerInstall>("""
                SELECT * FROM plugin_server_installs WHERE hashed_ip = @HashedIp
                """, new { HashedIp = hashedIp })).ToList();

            var reportedSlugs = pluginList.Select(p => p.Slug).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var existingBySlug = existing.ToDictionary(x => x.PluginSlug, StringComparer.OrdinalIgnoreCase);

            foreach (var plugin in pluginList)
            {
                if (existingBySlug.TryGetValue(plugin.Slug, out var existingInstall))
                {
                    if (existingInstall.UninstalledAt != null)
                    {
                        await conn.ExecuteAsync("""
                            UPDATE plugin_server_installs
                            SET version = @Version, btcpay_version = @BTCPayVersion, installed_at = @Now, updated_at = @Now, uninstalled_at = NULL, install_count = install_count + 1
                            WHERE hashed_ip = @HashedIp AND plugin_slug = @PluginSlug
                            """,
                            new { HashedIp = hashedIp, PluginSlug = plugin.Slug, Version = plugin.Version, BTCPayVersion = btcpayVersion, Now = now });
                    }
                    else
                    {
                        await conn.ExecuteAsync("""
                            UPDATE plugin_server_installs
                            SET version = @Version, btcpay_version = @BTCPayVersion, updated_at = @Now
                            WHERE hashed_ip = @HashedIp AND plugin_slug = @PluginSlug
                            """,
                            new { HashedIp = hashedIp, PluginSlug = plugin.Slug, Version = plugin.Version, BTCPayVersion = btcpayVersion, Now = now });
                    }
                }
                else
                {
                    await conn.ExecuteAsync("""
                        INSERT INTO plugin_server_installs
                            (hashed_ip, plugin_slug, version, btcpay_version, installed_at, updated_at, uninstalled_at, install_count)
                        VALUES
                            (@HashedIp, @PluginSlug, @Version, @BTCPayVersion, @Now, @Now, NULL, 1)
                        """,
                        new { HashedIp = hashedIp, PluginSlug = plugin.Slug, Version = plugin.Version, BTCPayVersion = btcpayVersion, Now = now });
                }
            }

            foreach (var install in existing.Where(x => x.UninstalledAt == null))
            {
                if (!reportedSlugs.Contains(install.PluginSlug))
                {
                    await conn.ExecuteAsync("""
                        UPDATE plugin_server_installs SET uninstalled_at = @Now
                        WHERE hashed_ip = @HashedIp AND plugin_slug = @PluginSlug
                        """,
                        new { HashedIp = hashedIp, PluginSlug = install.PluginSlug, Now = now });
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record server snapshot telemetry");
        }
    }

    public async Task<PluginStats> GetStats(string pluginSlug)
    {
        await using var conn = await connectionFactory.Open();
        var totalDownloads = await conn.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM plugin_downloads WHERE plugin_slug = @PluginSlug
            """,
            new { PluginSlug = pluginSlug });

        var installStats = await conn.QueryFirstOrDefaultAsync<(int TotalInstalls, int ActiveInstalls, int TotalUninstalls)>("""
            SELECT
                COALESCE(SUM(install_count), 0) AS TotalInstalls,
                COUNT(*) FILTER (WHERE uninstalled_at IS NULL) AS ActiveInstalls,
                COUNT(*) FILTER (WHERE uninstalled_at IS NOT NULL) AS TotalUninstalls
            FROM plugin_server_installs
            WHERE plugin_slug = @PluginSlug
            """,
            new { PluginSlug = pluginSlug });

        var versionBreakdown = (await conn.QueryAsync<VersionStat>("""
            SELECT version AS Version, COUNT(*) AS Count
            FROM plugin_server_installs
            WHERE plugin_slug = @PluginSlug AND uninstalled_at IS NULL
            GROUP BY version
            ORDER BY Count DESC
            """,
            new { PluginSlug = pluginSlug })).ToList();

        var btcpayVersionBreakdown = (await conn.QueryAsync<VersionStat>("""
            SELECT btcpay_version AS Version, COUNT(*) AS Count
            FROM plugin_server_installs
            WHERE plugin_slug = @PluginSlug AND uninstalled_at IS NULL
            GROUP BY btcpay_version
            ORDER BY Count DESC
            """,
            new { PluginSlug = pluginSlug })).ToList();

        return new PluginStats(
            TotalDownloads: totalDownloads,
            TotalInstalls: installStats.TotalInstalls,
            TotalUpdates: Math.Max(0, totalDownloads - installStats.TotalInstalls),
            ActiveInstalls: installStats.ActiveInstalls,
            TotalUninstalls: installStats.TotalUninstalls,
            VersionBreakdown: versionBreakdown,
            BTCPayVersionBreakdown: btcpayVersionBreakdown
        );
    }

    private static bool TryParseBTCPayUserAgent(string? userAgent, out string btcpayVersion)
    {
        btcpayVersion = string.Empty;
        if (string.IsNullOrWhiteSpace(userAgent))
            return false;

        var match = BTCPayUserAgentRegex.Match(userAgent);
        if (!match.Success)
            return false;

        btcpayVersion = match.Groups[1].Value;
        return true;
    }

    private static bool TryGetPublicIp(string? remoteIp, out string? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(remoteIp))
            return false;

        if (!IPAddress.TryParse(remoteIp, out var ip))
            return false;

        if (IsPrivateOrLoopback(ip))
            return false;

        result = remoteIp;
        return true;
    }

    private static bool IsPrivateOrLoopback(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
            return true;

        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            // 10.0.0.0/8
            if (bytes[0] == 10)
                return true;
            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                return true;
            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168)
                return true;
            // 169.254.0.0/16 (link-local)
            if (bytes[0] == 169 && bytes[1] == 254)
                return true;
        }
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            // fc00::/7 (unique local)
            if ((bytes[0] & 0xFE) == 0xFC)
                return true;
            // fe80::/10 (link-local)
            if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)
                return true;
        }
        return false;
    }

    private static string HashIp(string ip)
    {
        var bytes = Encoding.UTF8.GetBytes(ip);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }


}

public record PluginReport(string Slug, string Version);
public record PluginStats(
        int TotalDownloads,
        int TotalInstalls,
        int TotalUpdates,
        int ActiveInstalls,
        int TotalUninstalls,
        List<VersionStat> VersionBreakdown,
        List<VersionStat> BTCPayVersionBreakdown
    );

public record VersionStat(string Version, int Count);
