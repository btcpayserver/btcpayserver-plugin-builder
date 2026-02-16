namespace PluginBuilder.APIModels;

/// <summary>
/// Request model for releasing a plugin version.
/// When Signature is provided, performs a GPG-signed release.
/// When absent, performs a plain release.
/// </summary>
public class ReleaseVersionRequest
{
    /// <summary>
    /// Base64-encoded GPG detached signature of the manifest hash.
    /// </summary>
    public string? Signature { get; set; }
}
