using System.Security.Cryptography;
using Dapper;
using PluginBuilder.Authentication;

namespace PluginBuilder.Services;

// Personal API tokens for publishers, each scoped to one plugin the user owns. Only the hash is stored; the token is
// returned once, at creation.
public class PublisherAccessTokenService(DBConnectionFactory connections)
{
    public static readonly int[] LifetimeChoices = [30, 90, 365];

    public async Task<IReadOnlyList<TokenInfo>> List(string userId, CancellationToken cancellationToken = default)
    {
        await using var conn = await connections.Open(cancellationToken);
        return (await conn.QueryAsync<TokenInfo>(new CommandDefinition("""
            SELECT t.id, t.name, t.plugin_slug AS PluginSlug, t.created_at AS CreatedAt, t.expires_at AS ExpiresAt,
                   t.revoked_at AS RevokedAt, t.last_used_at AS LastUsedAt,
                   t.security_stamp IS DISTINCT FROM u."SecurityStamp" AS Invalidated
            FROM publisher_access_tokens t JOIN "AspNetUsers" u ON u."Id" = t.user_id
            WHERE t.user_id = @userId ORDER BY t.created_at DESC
            """, new { userId }, cancellationToken: cancellationToken))).ToList();
    }

    public async Task<IReadOnlyList<string>> OwnedPlugins(string userId, CancellationToken cancellationToken = default)
    {
        await using var conn = await connections.Open(cancellationToken);
        return (await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT plugin_slug FROM users_plugins WHERE user_id = @userId ORDER BY plugin_slug", new { userId },
            cancellationToken: cancellationToken))).ToList();
    }

    // expiresInDays null = no expiry. The token is only created for a plugin the user owns at this moment; ownership is
    // also rechecked on every request (OwnPlugin), so removing an owner ends their tokens' access too.
    // securityStamp must come from the authenticated session, so a credential change cannot renew a stale session's access.
    public async Task<IssuedToken> Create(string userId, string pluginSlug, string name, int? expiresInDays,
        string securityStamp,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100)
            throw new ArgumentException("Provide a token name of up to 100 characters.");
        if (expiresInDays is < 1 or > 365)
            throw new ArgumentException("Choose a lifetime of 1-365 days, or no expiry.");
        var id = Guid.NewGuid();
        var token = PublisherTokenAuthenticationHandler.Prefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        DateTimeOffset? expiresAt = expiresInDays is { } days ? DateTimeOffset.UtcNow.AddDays(days) : null;
        await using var conn = await connections.Open(cancellationToken);
        var count = await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO publisher_access_tokens(id, user_id, plugin_slug, name, token_hash, security_stamp, expires_at)
            SELECT @id, u."Id", up.plugin_slug, @name, @hash, @securityStamp, @expiresAt
            FROM "AspNetUsers" u JOIN users_plugins up ON up.user_id = u."Id"
            WHERE u."Id" = @userId AND up.plugin_slug = @pluginSlug AND u."SecurityStamp" = @securityStamp
            """, new { id, userId, pluginSlug, name = name.Trim(), hash = AdminTokenAuthenticationHandler.Hash(token), securityStamp, expiresAt },
            cancellationToken: cancellationToken));
        if (count != 1)
            throw new ArgumentException("You can only create a token for a plugin you own with a current session. Sign in again and try again.");
        return new IssuedToken(id, token, pluginSlug, expiresAt);
    }

    public async Task<bool> Revoke(string userId, Guid id, CancellationToken cancellationToken = default)
    {
        await using var conn = await connections.Open(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE publisher_access_tokens SET revoked_at = CURRENT_TIMESTAMP
            WHERE id = @id AND user_id = @userId AND revoked_at IS NULL
            """, new { id, userId }, cancellationToken: cancellationToken));
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM publisher_access_tokens WHERE id = @id AND user_id = @userId)", new { id, userId },
            cancellationToken: cancellationToken));
    }

    public record IssuedToken(Guid Id, string Token, string PluginSlug, DateTimeOffset? ExpiresAt);

    public class TokenInfo
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string PluginSlug { get; set; } = "";
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public DateTimeOffset? RevokedAt { get; set; }
        public DateTimeOffset? LastUsedAt { get; set; }
        public bool Invalidated { get; set; }
    }
}
