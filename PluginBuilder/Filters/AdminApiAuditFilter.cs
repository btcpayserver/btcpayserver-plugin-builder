using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Mvc.Filters;
using PluginBuilder.Authentication;
using PluginBuilder.Services;

namespace PluginBuilder.Filters;

public class AdminApiAuditFilter(DBConnectionFactory connections, ILogger<AdminApiAuditFilter> logger) : IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var http = context.HttpContext;
        if (!http.Request.Path.StartsWithSegments("/api/v1/admin") || http.User.Identity?.IsAuthenticated != true)
        {
            await next();
            return;
        }
        var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        Guid? tokenId = Guid.TryParse(http.User.FindFirstValue(PluginBuilderAuthenticationSchemes.TokenIdClaim), out var parsed) ? parsed : null;
        long id;
        // Fail before running the action if its audit record cannot be persisted.
        await using (var conn = await connections.Open(http.RequestAborted))
            id = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO admin_api_audit(user_id, token_id, method, path) VALUES (@userId, @tokenId, @method, @path) RETURNING id
                """, new { userId, tokenId, method = http.Request.Method, path = http.Request.Path.Value }, cancellationToken: http.RequestAborted));
        var statusCode = 500;
        try
        {
            var executed = await next();
            statusCode = executed.Exception is null || executed.ExceptionHandled ? http.Response.StatusCode : 500;
        }
        finally
        {
            // Preserve started-but-incomplete rows on crash/failure; never store
            // credentials, request/response bodies, or query-string values.
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
