namespace PluginBuilder.Events;

public class BuildLogUpdated
{
    public BuildLogUpdated(FullBuildId fullBuildId, string log)
    {
        FullBuildId = fullBuildId;
        Log = log;
    }
        
    public FullBuildId FullBuildId { get; }
    public string Log { get; set; }
}
