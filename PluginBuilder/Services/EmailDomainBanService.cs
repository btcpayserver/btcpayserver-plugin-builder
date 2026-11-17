using Dapper;
using Newtonsoft.Json.Linq;
using Npgsql;
using PluginBuilder.Util;

namespace PluginBuilder.Services;

public sealed class EmailDomainBanService(DBConnectionFactory connections, AccountModerationService moderation)
{
    public static string? NormalizeDomain(string? value) =>
        DisposableEmailDomains.NormalizeDomain(value);

    public async Task<bool> IsBanned(NpgsqlConnection conn, NpgsqlTransaction tx, string email,
        CancellationToken ct = default)
    {
        var domains = await conn.QueryAsync<string>(new CommandDefinition("SELECT domain FROM email_domain_bans",
            transaction: tx, cancellationToken: ct));
        return DisposableEmailDomains.IsDisposable(email, domains.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    public async Task<long> Create(string domain, string reason, string? actor, string? tokenId,
        CancellationToken ct = default)
    {
        domain = NormalizeDomain(domain) ?? throw new ArgumentException("Invalid email domain.", nameof(domain));
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 1000)
            throw new ArgumentException("Provide a reason between 1 and 1000 characters.", nameof(reason));
        reason = reason.Trim();

        await using var conn = await connections.Open(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await AccountModerationService.Serialize(conn, tx, ct);
        var id = await conn.QuerySingleOrDefaultAsync<long?>(new CommandDefinition("""
            INSERT INTO email_domain_bans (domain, reason, banned_by) VALUES (@domain, @reason, @actor)
            ON CONFLICT (domain) DO NOTHING RETURNING id
            """, new { domain, reason, actor }, tx, cancellationToken: ct));
        if (id is null)
            return await conn.QuerySingleAsync<long>(new CommandDefinition(
                "SELECT id FROM email_domain_bans WHERE domain = @domain", new { domain }, tx, cancellationToken: ct));

        await moderation.Emit(conn, tx, "email_domain.banned", new JObject
        {
            ["banId"] = id.Value, ["domain"] = domain, ["reason"] = reason
        }, actor, tokenId, ct);
        await tx.CommitAsync(ct);
        return id.Value;
    }

    public async Task<bool> Revoke(long banId, string reason, string? actor, string? tokenId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 1000)
            throw new ArgumentException("Provide a reason between 1 and 1000 characters.", nameof(reason));
        await using var conn = await connections.Open(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await AccountModerationService.Serialize(conn, tx, ct);
        var domain = await conn.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "DELETE FROM email_domain_bans WHERE id = @banId RETURNING domain", new { banId }, tx, cancellationToken: ct));
        if (domain is null)
            return false;
        await moderation.Emit(conn, tx, "email_domain.ban_revoked", new JObject
        {
            ["banId"] = banId, ["domain"] = domain, ["reason"] = reason.Trim()
        }, actor, tokenId, ct);
        await tx.CommitAsync(ct);
        return true;
    }
}
