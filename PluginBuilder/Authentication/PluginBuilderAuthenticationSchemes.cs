namespace PluginBuilder.Authentication;

public class PluginBuilderAuthenticationSchemes
{
    public const string BasicAuth = "PluginBuilder.BasicAuth";
    public const string AdminToken = "PluginBuilder.AdminToken";
    public const string PublisherToken = "PluginBuilder.PublisherToken";
    public const string AdminApi = BasicAuth + "," + AdminToken;
    public const string PublisherApi = BasicAuth + "," + PublisherToken;
    public const string TokenIdClaim = "plugin-builder:admin-token-id";
    public const string PublisherTokenIdClaim = "plugin-builder:publisher-token-id";
    // The one plugin a publisher token may act on; the OwnPlugin requirement refuses every other slug.
    public const string PublisherTokenPluginClaim = "plugin-builder:publisher-token-plugin";
}
