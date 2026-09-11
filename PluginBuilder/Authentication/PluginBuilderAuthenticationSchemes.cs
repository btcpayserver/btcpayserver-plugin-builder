namespace PluginBuilder.Authentication;

public class PluginBuilderAuthenticationSchemes
{
    public const string BasicAuth = "PluginBuilder.BasicAuth";
    public const string AdminToken = "PluginBuilder.AdminToken";
    public const string AdminApi = BasicAuth + "," + AdminToken;
    public const string TokenIdClaim = "plugin-builder:admin-token-id";
}
