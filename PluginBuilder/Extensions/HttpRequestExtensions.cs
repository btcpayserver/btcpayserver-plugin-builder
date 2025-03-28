namespace PluginBuilder.Extensions;

public static class HttpRequestExtensions
{
    public static string GetCurrentUrlWithQueryString(this HttpRequest request)
    {
        return string.Concat(
            request.Scheme,
            "://",
            request.Host.ToUriComponent(),
            request.PathBase.ToUriComponent(),
            request.Path.ToUriComponent(),
            request.QueryString.ToUriComponent());
    }
}
