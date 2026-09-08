using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using PluginBuilder.Services;
using PluginBuilder.Util;

namespace PluginBuilder.Authentication;

public class AdminTokenAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, DBConnectionFactory connections) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Prefix = "pb_admin_";
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();
        var token = header[7..].Trim();
        if (!token.StartsWith(Prefix, StringComparison.Ordinal) || token.Length != Prefix.Length + 64)
            return AuthenticateResult.Fail("Invalid admin access token.");
        await using var conn = await connections.Open(Context.RequestAborted);
        // Recheck revocation, expiry, security stamp, lockout and roles on every request.
        var identity = await conn.QuerySingleOrDefaultAsync<TokenIdentity>(new CommandDefinition("""
            UPDATE admin_access_tokens t SET last_used_at = CURRENT_TIMESTAMP FROM "AspNetUsers" u
            WHERE t.user_id = u."Id" AND t.token_hash = @hash AND t.revoked_at IS NULL AND t.expires_at > CURRENT_TIMESTAMP
              AND t.security_stamp IS NOT DISTINCT FROM u."SecurityStamp"
              AND (NOT u."LockoutEnabled" OR u."LockoutEnd" IS NULL OR u."LockoutEnd" <= CURRENT_TIMESTAMP)
            RETURNING t.id AS TokenId, t.user_id AS UserId,
              EXISTS(SELECT 1 FROM "AspNetUserRoles" ur JOIN "AspNetRoles" r ON r."Id" = ur."RoleId"
                     WHERE ur."UserId" = t.user_id AND r."Name" = 'ServerAdmin') AS IsAdmin
            """, new { hash = Hash(token) }, cancellationToken: Context.RequestAborted));
        if (identity is null)
            return AuthenticateResult.Fail("Invalid admin access token.");
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, identity.UserId),
            new(PluginBuilderAuthenticationSchemes.TokenIdClaim, identity.TokenId.ToString())
        };
        if (identity.IsAdmin)
            claims.Add(new Claim(ClaimTypes.Role, Roles.ServerAdmin));
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name));
    }

    private sealed class TokenIdentity
    {
        public Guid TokenId { get; set; }
        public string UserId { get; set; } = "";
        public bool IsAdmin { get; set; }
    }
}
