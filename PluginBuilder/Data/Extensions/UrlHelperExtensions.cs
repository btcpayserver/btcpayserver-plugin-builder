using Microsoft.AspNetCore.Mvc;

namespace PluginBuilder.Data.Extensions
{
    public static class UrlHelperExtensions
    {
#nullable enable
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
#nullable restore

    }
}
