using Microsoft.AspNetCore.Mvc;

namespace PluginBuilder.Util.Extensions;

public static class UrlHelperExtensions
{
    public static string? EnsureLocal(this IUrlHelper helper, string? url, HttpRequest? httpRequest = null)
    {
        if (url is null)
            return url;

        url = url.Trim();
        if (helper.IsLocalUrl(url))
            return url;

        if (httpRequest is null)
            return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
            return null;

        var reqScheme = httpRequest.Scheme;
        var reqPort = httpRequest.Host.Port ?? (httpRequest.IsHttps ? 443 : 80);
        var uriPort = u.IsDefaultPort
            ? string.Equals(u.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80
            : u.Port;
        if (string.Equals(u.Host, httpRequest.Host.Host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(u.Scheme, reqScheme, StringComparison.OrdinalIgnoreCase)
            && uriPort == reqPort)
            return url;

        return null;
    }
}
