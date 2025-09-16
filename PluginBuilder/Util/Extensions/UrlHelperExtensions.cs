using Microsoft.AspNetCore.Mvc;

namespace PluginBuilder.Util.Extensions;

public static class UrlHelperExtensions
{
    public static string? EnsureLocal(this IUrlHelper helper, string? url, HttpRequest? httpRequest = null)
    {
        if (url is null || helper.IsLocalUrl(url))
            return url;
        if (httpRequest is null)
            return null;
        if (Uri.TryCreate(url, UriKind.Absolute, out var r) && r.Host.Equals(httpRequest.Host.Host) && (!httpRequest.IsHttps || r.Scheme == "https"))
            return url;
        return null;
    }
}
