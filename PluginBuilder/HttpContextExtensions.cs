namespace PluginBuilder
{
    public static class HttpContextExtensions
    {
        public static void SetPluginSlug(this HttpContext httpContext, PluginSlug pluginSlug)
        {
            httpContext.Items["PLUGIN_SLUG"] = pluginSlug;
        }
        public static PluginSlug? GetPluginSlug(this HttpContext httpContext)
        {
            return httpContext.Items["PLUGIN_SLUG"] as PluginSlug;
        }
    }
}
