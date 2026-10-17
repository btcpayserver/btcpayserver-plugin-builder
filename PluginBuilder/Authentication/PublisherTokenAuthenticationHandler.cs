using System.Security.Claims;
using System.Text.Encodings.Web;
using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.Services;

namespace PluginBuilder.Authentication;

// A publisher's personal API token: `Authorization: Bearer pb_pub_...`. It authenticates as its user, scoped to one
// plugin or all owned plugins (PublisherTokenPluginClaim, enforced by OwnPlugin). It carries no roles, so even an admin's
// token is only a publisher credential. It works for accounts with two-factor authentication, which Basic auth refuses.
public class PublisherTokenAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, DBConnectionFactory connections, UserVerifiedLogic userVerifiedLogic)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Prefix = "pb_pub_";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();
        var token = header[7..].Trim();
        // Other bearer tokens are not ours to judge: leave them to the schemes that issued them.
        if (!token.StartsWith(Prefix, StringComparison.Ordinal))
            return AuthenticateResult.NoResult();
        if (token.Length != Prefix.Length + 64)
            return AuthenticateResult.Fail("Invalid publisher access token.");
        await using var conn = await connections.Open(Context.RequestAborted);
        // Recheck revocation, expiry, security stamp and lockout on every request (the same rules as admin tokens).
        var identity = await conn.QuerySingleOrDefaultAsync<TokenIdentity>(new CommandDefinition("""
            UPDATE publisher_access_tokens t SET last_used_at = CURRENT_TIMESTAMP FROM "AspNetUsers" u
            WHERE t.user_id = u."Id" AND t.token_hash = @hash AND t.revoked_at IS NULL
              AND (t.expires_at IS NULL OR t.expires_at > CURRENT_TIMESTAMP)
              AND t.security_stamp IS NOT DISTINCT FROM u."SecurityStamp"
              AND (NOT u."LockoutEnabled" OR u."LockoutEnd" IS NULL OR u."LockoutEnd" <= CURRENT_TIMESTAMP)
            RETURNING t.id AS TokenId, t.user_id AS UserId, t.plugin_slug AS PluginSlug
            """, new { hash = AdminTokenAuthenticationHandler.Hash(token) }, cancellationToken: Context.RequestAborted));
        if (identity is null)
            return AuthenticateResult.Fail("Invalid publisher access token.");
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, identity.UserId),
            new(PluginBuilderAuthenticationSchemes.PublisherTokenIdClaim, identity.TokenId.ToString()),
            new(PluginBuilderAuthenticationSchemes.PublisherTokenPluginClaim,
                identity.PluginSlug ?? PluginBuilderAuthenticationSchemes.AllPublisherPlugins)
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        if (userVerifiedLogic.IsEmailVerificationRequiredForLogin &&
            !await userVerifiedLogic.IsUserEmailVerifiedForLogin(principal))
            return AuthenticateResult.Fail("Invalid publisher access token.");
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    private sealed class TokenIdentity
    {
        public Guid TokenId { get; set; }
        public string UserId { get; set; } = "";
        public string? PluginSlug { get; set; }
    }
}
