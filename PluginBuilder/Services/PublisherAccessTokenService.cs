using System.Security.Cryptography;
using Dapper;
using PluginBuilder.Authentication;

namespace PluginBuilder.Services;

// Personal API tokens for publishers, scoped to one plugin or all plugins the user owns. Only the hash is stored; the token is
// returned once, at creation.
public class PublisherAccessTokenService(DBConnectionFactory connections, ILogger<PublisherAccessTokenService> logger)
{
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

    // expiresInDays null = no expiry. A specific plugin must be owned at creation; AllPublisherPlugins covers current
    // and future plugins. Ownership is rechecked on every request (OwnPlugin), so removing an owner ends access too.
    // securityStamp must come from the authenticated session, so a credential change cannot renew a stale session's access.
    public async Task<IssuedToken> Create(string userId, string pluginSlug, string name, int? expiresInDays,
        string securityStamp,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100)
            throw new ArgumentException("Provide a token name of up to 100 characters.");
        if (expiresInDays is < 1 or > 365)
            throw new ArgumentException("Choose a lifetime of 1-365 days, or no expiry.");
        var allPlugins = pluginSlug == PluginBuilderAuthenticationSchemes.AllPublisherPlugins;
        if (!allPlugins && !PluginSlug.IsValidSlugName(pluginSlug))
            throw new ArgumentException("Choose a plugin or all your plugins.");
        string? scopePlugin = allPlugins ? null : pluginSlug;
        var id = Guid.NewGuid();
        var token = PublisherTokenAuthenticationHandler.Prefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        DateTimeOffset? expiresAt = expiresInDays is { } days ? DateTimeOffset.UtcNow.AddDays(days) : null;
        await using var conn = await connections.Open(cancellationToken);
        var count = await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO publisher_access_tokens(id, user_id, plugin_slug, name, token_hash, security_stamp, expires_at)
            SELECT @id, u."Id", @scopePlugin, @name, @hash, @securityStamp, @expiresAt
            FROM "AspNetUsers" u
            WHERE u."Id" = @userId AND u."SecurityStamp" = @securityStamp
              AND NOT EXISTS (SELECT 1 FROM admin_account_locks l
                              WHERE l.user_id = u."Id" AND l.locked_until > CURRENT_TIMESTAMP)
              AND (CAST(@scopePlugin AS text) IS NULL OR EXISTS (
                  SELECT 1 FROM users_plugins up WHERE up.user_id = u."Id" AND up.plugin_slug = @scopePlugin))
            """, new { id, userId, scopePlugin, name = name.Trim(), hash = AdminTokenAuthenticationHandler.Hash(token), securityStamp, expiresAt },
            cancellationToken: cancellationToken));
        if (count != 1)
            throw new ArgumentException("You can only create a token for a plugin you own with a current session. Your account must not be suspended. Sign in again and try again.");
        logger.LogInformation("Publisher token created: user={UserId} token={TokenId} scope={PluginScope}",
            userId, id, scopePlugin ?? PluginBuilderAuthenticationSchemes.AllPublisherPlugins);
        return new IssuedToken(id, token, scopePlugin, expiresAt);
    }

    public async Task<bool> Revoke(string userId, Guid id, CancellationToken cancellationToken = default)
    {
        await using var conn = await connections.Open(cancellationToken);
        var revoked = await conn.QuerySingleOrDefaultAsync<TokenInfo>(new CommandDefinition("""
            UPDATE publisher_access_tokens SET revoked_at = CURRENT_TIMESTAMP
            WHERE id = @id AND user_id = @userId AND revoked_at IS NULL
            RETURNING id, plugin_slug AS PluginSlug
            """, new { id, userId }, cancellationToken: cancellationToken));
        if (revoked is not null)
        {
            logger.LogInformation("Publisher token revoked: user={UserId} token={TokenId} scope={PluginScope}",
                userId, id, revoked.PluginSlug ?? PluginBuilderAuthenticationSchemes.AllPublisherPlugins);
            return true;
        }
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM publisher_access_tokens WHERE id = @id AND user_id = @userId)", new { id, userId },
            cancellationToken: cancellationToken));
    }

    public record IssuedToken(Guid Id, string Token, string? PluginSlug, DateTimeOffset? ExpiresAt);

    public class TokenInfo
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string? PluginSlug { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public DateTimeOffset? RevokedAt { get; set; }
        public DateTimeOffset? LastUsedAt { get; set; }
        public bool Invalidated { get; set; }
    }
}
