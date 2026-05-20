using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using PluginBuilder.DataModels;

namespace PluginBuilder.Services;

public class TelemetryService(DBConnectionFactory connectionFactory, ILogger<TelemetryService> logger)
{
    private static readonly Regex BTCPayUserAgentRegex = new(@"^BTCPayServer/(\d+\.\d+\.\d+)", RegexOptions.Compiled);

    public async Task RecordPluginDownload(string pluginSlug, string version, string? userAgent, string? remoteIp,
        string? xOriginalFor = null, string? xForwardedFor = null)
    {
        try
        {
            if (!TryParseBTCPayUserAgent(userAgent, out var btcpayVersion))
                return;

            if (!TryGetPublicIp(remoteIp, xOriginalFor, xForwardedFor, out var ip))
                return;

            var hashedIp = HashIp(ip!);
            var now = DateTimeOffset.UtcNow;

            await using var conn = await connectionFactory.Open();
            var existing = await conn.QueryFirstOrDefaultAsync<PluginServerInstall>("""
                SELECT * FROM plugin_server_installs WHERE hashed_ip = @HashedIp AND plugin_slug = @PluginSlug
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


    public async Task RecordServerSnapshot(string? remoteIp, string userAgent, IEnumerable<PluginReport> plugins,
        string? xOriginalFor = null, string? xForwardedFor = null)
    {
        try
        {
            if (!TryParseBTCPayUserAgent(userAgent, out var btcpayVersion))
                return;

            if (!TryGetPublicIp(remoteIp, xOriginalFor, xForwardedFor, out var ip))
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
                        UPDATE plugin_server_installs SET uninstalled_at = @Now, install_count = GREATEST(0, install_count - 1)
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

        var installStats = await conn.QueryFirstOrDefaultAsync<(int TotalInstalls, int ActiveInstalls, int TotalUninstalls)>("""
            SELECT
                COALESCE(SUM(install_count), 0) AS TotalInstalls,
                COALESCE(COUNT(*) FILTER (WHERE uninstalled_at IS NULL), 0) AS ActiveInstalls,
                COALESCE(COUNT(*) FILTER (WHERE uninstalled_at IS NOT NULL), 0) AS TotalUninstalls
            FROM plugin_server_installs WHERE plugin_slug = @PluginSlug
            """, new { PluginSlug = pluginSlug });

        var totalInstalls = installStats == default ? 0 : installStats.TotalInstalls;
        var activeInstalls = installStats == default ? 0 : installStats.ActiveInstalls;
        var totalUninstalls = installStats == default ? 0 : installStats.TotalUninstalls;

        var versionBreakdown = (await conn.QueryAsync<VersionStat>("""
            SELECT COALESCE(version, 'unknown') AS Version, COALESCE(COUNT(*), 0) AS Count
            FROM plugin_server_installs
            WHERE plugin_slug = @PluginSlug AND uninstalled_at IS NULL AND version IS NOT NULL
            GROUP BY version
            ORDER BY Count DESC
            """, new { PluginSlug = pluginSlug })).ToList();

        var btcpayVersionBreakdown = (await conn.QueryAsync<VersionStat>("""
            SELECT COALESCE(btcpay_version, 'unknown') AS Version, COALESCE(COUNT(*), 0) AS Count
            FROM plugin_server_installs
            WHERE plugin_slug = @PluginSlug AND uninstalled_at IS NULL AND btcpay_version IS NOT NULL
            GROUP BY btcpay_version
            ORDER BY Count DESC
            """, new { PluginSlug = pluginSlug })).ToList();

        return new PluginStats(
            TotalInstalls: installStats.TotalInstalls,
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

    private static bool TryGetPublicIp(string? remoteIp, string? xOriginalFor, string? xForwardedFor, out string? result)
    {
        result = null;
        var candidates = new[] { xOriginalFor, xForwardedFor, remoteIp };
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            var raw = candidate.Split(',')[0].Trim();
            
            if (raw.Contains("]:"))
                raw = raw.Substring(1, raw.IndexOf(']') - 1);
            else if (raw.Contains(':') && !raw.Contains('.'))
                raw = raw.Split(':')[0];

            if (!IPAddress.TryParse(raw, out var ip))
                continue;

            if (IsPrivateOrLoopback(ip))
                continue;

            result = raw;
            return true;
        }
        return false;
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
            if (bytes[0] == 10)
                return true;
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                return true;
            if (bytes[0] == 192 && bytes[1] == 168)
                return true;
            if (bytes[0] == 169 && bytes[1] == 254)
                return true;
        }
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if ((bytes[0] & 0xFE) == 0xFC)
                return true;
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
        int TotalInstalls,
        int ActiveInstalls,
        int TotalUninstalls,
        List<VersionStat> VersionBreakdown,
        List<VersionStat> BTCPayVersionBreakdown
    );

public record VersionStat(string Version, long Count);
