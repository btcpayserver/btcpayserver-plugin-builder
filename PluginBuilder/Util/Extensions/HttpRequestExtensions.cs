namespace PluginBuilder.Util.Extensions;

public static class HttpRequestExtensions
{
    public static bool IsEmbeddedMode(this HttpRequest request)
        => string.Equals(request.Query["embed"], "1", StringComparison.Ordinal);
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
