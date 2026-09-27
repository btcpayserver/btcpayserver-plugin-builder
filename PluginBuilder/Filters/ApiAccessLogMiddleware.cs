using System.Globalization;
using System.Security.Claims;

namespace PluginBuilder.Filters;

public class ApiAccessLogMiddleware(RequestDelegate next, ILogger<ApiAccessLogMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext http)
    {
        // Error-page re-execution uses /errors, so it must not produce a second entry.
        var isApiRequest = http.Request.Path.StartsWithSegments("/api/v1");
        await next(http);

        if (!isApiRequest || http.Response.StatusCode is not (401 or 403))
            return;

        // Use the address resolved by the application's forwarded-header middleware,
        // never a raw client-supplied forwarding header. Do not log query strings or credentials.
        var address = http.Connection.RemoteIpAddress;
        if (address?.IsIPv4MappedToIPv6 is true)
            address = address.MapToIPv4();
        var authorization = http.Request.Headers.Authorization.ToString();
        var credentialType = string.IsNullOrEmpty(authorization) ? "none"
            : authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase) ? "basic"
            : authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? "bearer"
            : "other";
        var userId = http.User.Identity?.IsAuthenticated is true
            ? http.User.FindFirstValue(ClaimTypes.NameIdentifier)
            : null;

        logger.LogInformation(
            "API access denied: status={StatusCode} method={Method} path={Path} ip={ClientIp} auth={CredentialType} user={UserId} requestId={RequestId} userAgent={UserAgent}",
            http.Response.StatusCode, SafeField(http.Request.Method, 16), SafeField(http.Request.Path.Value, 256),
            address?.ToString() ?? "unknown", credentialType, SafeField(userId, 128),
            SafeField(http.TraceIdentifier, 128), SafeField(http.Request.Headers.UserAgent.ToString(), 160));
    }

    private static string SafeField(string? value, int limit)
    {
        if (string.IsNullOrEmpty(value))
            return "-";

        var chars = value.Take(limit).Select(c => char.IsControl(c) ||
            char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                ? ' ' : c).ToArray();
        return new string(chars) + (value.Length > limit ? "..." : "");
    }
}
