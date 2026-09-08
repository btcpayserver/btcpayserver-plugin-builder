using System.Security.Cryptography;
using Dapper;
using PluginBuilder.Authentication;

namespace PluginBuilder.Services;

public class AdminAccessTokenService(DBConnectionFactory connections)
{
    public async Task<IReadOnlyList<TokenInfo>> List(string userId, CancellationToken cancellationToken = default)
    {
        await using var conn = await connections.Open(cancellationToken);
        return (await conn.QueryAsync<TokenInfo>(new CommandDefinition("""
            SELECT id, name, created_at AS CreatedAt, expires_at AS ExpiresAt, revoked_at AS RevokedAt, last_used_at AS LastUsedAt
            FROM admin_access_tokens WHERE user_id = @userId ORDER BY created_at DESC
            """, new { userId }, cancellationToken: cancellationToken))).ToList();
    }

    public async Task<IssuedToken> Create(string userId, string name, int expiresInDays, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || expiresInDays is < 1 or > 365)
            throw new ArgumentException("Provide a name up to 100 characters and a lifetime of 1–365 days.");
        var id = Guid.NewGuid();
        var token = AdminTokenAuthenticationHandler.Prefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var expiresAt = DateTimeOffset.UtcNow.AddDays(expiresInDays);
        await using var conn = await connections.Open(cancellationToken);
        await using var transaction = await conn.BeginTransactionAsync(cancellationToken);
        var count = await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO admin_access_tokens(id, user_id, name, token_hash, security_stamp, expires_at)
            SELECT @id, "Id", @name, @hash, "SecurityStamp", @expiresAt FROM "AspNetUsers" WHERE "Id" = @userId
            """, new { id, userId, name = name.Trim(), hash = AdminTokenAuthenticationHandler.Hash(token), expiresAt },
            transaction, cancellationToken: cancellationToken));
        if (count != 1)
            throw new InvalidOperationException("Admin account no longer exists.");
        await conn.ExecuteAsync(new CommandDefinition("""
            SELECT emit_admin_event('admin.token_created', jsonb_build_object('userId', @userId::text, 'tokenId', @id::uuid, 'name', @name::text))
            """, new { userId, id, name = name.Trim() }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return new IssuedToken(id, token, expiresAt);
    }

    public async Task<bool> Revoke(string userId, Guid id, CancellationToken cancellationToken = default)
    {
        await using var conn = await connections.Open(cancellationToken);
        await using var transaction = await conn.BeginTransactionAsync(cancellationToken);
        var changed = await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE admin_access_tokens SET revoked_at = CURRENT_TIMESTAMP
            WHERE id = @id AND user_id = @userId AND revoked_at IS NULL
            """, new { id, userId }, transaction, cancellationToken: cancellationToken));
        if (changed == 1)
            await conn.ExecuteAsync(new CommandDefinition("""
                SELECT emit_admin_event('admin.token_revoked', jsonb_build_object('userId', @userId::text, 'tokenId', @id::uuid))
                """, new { id, userId }, transaction, cancellationToken: cancellationToken));
        var exists = changed == 1 || await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM admin_access_tokens WHERE id = @id AND user_id = @userId)", new { id, userId }, transaction,
            cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return exists;
    }

    public record IssuedToken(Guid Id, string Token, DateTimeOffset ExpiresAt);
    public class TokenInfo
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public DateTimeOffset? RevokedAt { get; set; }
        public DateTimeOffset? LastUsedAt { get; set; }
    }
}
