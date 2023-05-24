namespace PluginBuilder.Events;

public class BuildChanged
{
    public BuildChanged(FullBuildId fullBuildId, string eventName)
    {
        FullBuildId = fullBuildId;
        EventName = eventName;
    }

    public FullBuildId FullBuildId { get; init; }
    public string EventName { get; init; }
    public string? BuildInfo { get; init; }
    public string? ManifestInfo { get; init; }
}
