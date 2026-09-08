using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Authentication;
using PluginBuilder.Authentication;
using PluginBuilder.Services;

namespace PluginBuilder.Filters;

public class AdminApiAuditMiddleware(RequestDelegate next, ILogger<AdminApiAuditMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext http, DBConnectionFactory connections)
    {
        if (!http.Request.Path.StartsWithSegments("/api/v1/admin") || http.GetEndpoint() is null)
        {
            await next(http);
            return;
        }
        // Authenticate for attribution only. Authorization still enforces each
        // endpoint's own schemes; a token cannot gain access to Basic-only routes.
        ClaimsPrincipal? actor = null;
        foreach (var scheme in new[] { PluginBuilderAuthenticationSchemes.BasicAuth, PluginBuilderAuthenticationSchemes.AdminToken })
        {
            var result = await http.AuthenticateAsync(scheme);
            if (result.Succeeded) actor = result.Principal;
        }
        var userId = actor?.FindFirstValue(ClaimTypes.NameIdentifier);
        Guid? tokenId = Guid.TryParse(actor?.FindFirstValue(PluginBuilderAuthenticationSchemes.TokenIdClaim), out var parsed) ? parsed : null;
        long id;
        await using (var conn = await connections.Open(http.RequestAborted))
            id = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO admin_api_audit(user_id, token_id, method, path) VALUES (@userId, @tokenId, @method, @path) RETURNING id
                """, new { userId, tokenId, method = http.Request.Method, path = http.Request.Path.Value }, cancellationToken: http.RequestAborted));
        var statusCode = 500;
        try
        {
            await next(http);
            statusCode = http.Response.StatusCode;
        }
        finally
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await using var conn = await connections.Open(timeout.Token);
                await conn.ExecuteAsync(new CommandDefinition("""
                    UPDATE admin_api_audit SET completed_at = CURRENT_TIMESTAMP, status_code = @statusCode WHERE id = @id
                    """, new { id, statusCode }, cancellationToken: timeout.Token));
            }
            catch (Exception ex) { logger.LogError("Unable to complete admin API audit record {AuditId}: {ErrorType}", id, ex.GetType().Name); }
        }
    }
}
