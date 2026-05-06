namespace PluginBuilder.Services;

public class ServerEnvironment
{
    public ServerEnvironment(IConfiguration configuration)
    {
        CheatMode = configuration.GetValue<bool?>("CHEAT_MODE") ?? false;
        EnableLocalArtifactDownloadProxy = configuration.GetValue<bool?>("ENABLE_LOCAL_ARTIFACT_DOWNLOAD_PROXY") ?? false;
    }

    public bool CheatMode { get; set; }
    public bool EnableLocalArtifactDownloadProxy { get; set; }
}
