using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Dapper;
using Newtonsoft.Json.Linq;
using Npgsql;

namespace PluginBuilder.Services;

public sealed class IpBanService(DBConnectionFactory connections, AccountModerationService moderation)
{
    public static string? NormalizeNetwork(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var parts = value.Trim().Split('/');
        if (parts.Length > 2 || parts[0].Contains('%') || !IPAddress.TryParse(parts[0], out var address))
            return null;
        // Reject shorthand, integer and octal IPv4 spellings in manual moderation requests.
        if (address.AddressFamily == AddressFamily.InterNetwork && parts[0] != address.ToString())
            return null;
        int? prefix = null;
        if (parts.Length == 2)
        {
            if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                return null;
            prefix = parsed;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            // A mapped IPv6 host is the same client as its IPv4 representation.
            if (prefix is not (null or 128))
                return null;
            address = address.MapToIPv4();
            prefix = 32;
        }
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return prefix is null or 32 ? address + "/32" : null;
        if (address.AddressFamily != AddressFamily.InterNetworkV6 || prefix is not (null or 64 or 128))
            return null;
        if (prefix == 64)
        {
            var bytes = address.GetAddressBytes();
            Array.Clear(bytes, 8, 8);
            return new IPAddress(bytes) + "/64";
        }
        return address + "/128";
    }

    public Task<bool> IsBanned(NpgsqlConnection conn, NpgsqlTransaction tx, IPAddress address,
        CancellationToken ct = default)
    {
        // Scope IDs describe a local interface and are not part of an IP address stored by PostgreSQL.
        var host = address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : new IPAddress(address.GetAddressBytes()).ToString();
        return conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(SELECT 1 FROM ip_bans WHERE CAST(@host AS inet) <<= network)
            """, new { host }, tx, cancellationToken: ct));
    }

    public async Task<long> Create(string network, string reason, string? actor, string? tokenId,
        CancellationToken ct = default)
    {
        network = NormalizeNetwork(network) ?? throw new ArgumentException("Invalid IP address or network.", nameof(network));
        await using var conn = await connections.Open(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await AccountModerationService.Serialize(conn, tx, ct);
        var id = await Insert(conn, tx, network, reason.Trim(), actor, tokenId, ct);
        await tx.CommitAsync(ct);
        return id;
    }

    public async Task<bool> Revoke(long banId, string reason, string? actor, string? tokenId,
        CancellationToken ct = default)
    {
        await using var conn = await connections.Open(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await AccountModerationService.Serialize(conn, tx, ct);
        var network = await conn.QuerySingleOrDefaultAsync<string>(new CommandDefinition("""
            DELETE FROM ip_bans WHERE id = @banId RETURNING network::text
            """, new { banId }, tx, cancellationToken: ct));
        if (network is null)
            return false;
        await moderation.Emit(conn, tx, "ip.ban_revoked", new JObject
        {
            ["banId"] = banId, ["network"] = network, ["reason"] = reason.Trim()
        }, actor, tokenId, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    // The caller holds the exclusive moderation serialization lock and commits this ban with its account locks.
    public async Task<string?> BanSignupIp(NpgsqlConnection conn, NpgsqlTransaction tx, string userId,
        string reason, string? actor, string? tokenId, CancellationToken ct = default)
    {
        var address = await conn.QuerySingleOrDefaultAsync<string>(new CommandDefinition("""
            SELECT host("SignupIp") FROM "AspNetUsers"
            WHERE "Id" = @userId AND "CreatedAt" > clock_timestamp() - INTERVAL '7 days'
            """, new { userId }, tx, cancellationToken: ct));
        var network = address is null ? null : NormalizeNetwork(address);
        if (network is null)
            return null;
        await Insert(conn, tx, network, reason.Trim(), actor, tokenId, ct);
        return network;
    }

    private async Task<long> Insert(NpgsqlConnection conn, NpgsqlTransaction tx, string network,
        string reason, string? actor, string? tokenId, CancellationToken ct)
    {
        var id = await conn.QuerySingleOrDefaultAsync<long?>(new CommandDefinition("""
            INSERT INTO ip_bans (network, reason, banned_by) VALUES (CAST(@network AS cidr), @reason, @actor)
            ON CONFLICT (network) DO NOTHING RETURNING id
            """, new { network, reason, actor }, tx, cancellationToken: ct));
        if (id is null)
            return await conn.QuerySingleAsync<long>(new CommandDefinition("""
                SELECT id FROM ip_bans WHERE network = CAST(@network AS cidr)
                """, new { network }, tx, cancellationToken: ct));
        await moderation.Emit(conn, tx, "ip.banned", new JObject
        {
            ["banId"] = id.Value, ["network"] = network, ["reason"] = reason
        }, actor, tokenId, ct);
        return id.Value;
    }
}
