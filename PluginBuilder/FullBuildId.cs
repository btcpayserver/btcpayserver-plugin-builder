namespace PluginBuilder;

public record FullBuildId
{
    public FullBuildId(PluginSlug PluginSlug, long BuildId)
    {
        ArgumentNullException.ThrowIfNull(PluginSlug);
        if (BuildId < 0)
            throw new ArgumentException("BuildId should be more than 0", nameof(BuildId));
        this.PluginSlug = PluginSlug;
        this.BuildId = BuildId;
    }

    public PluginSlug PluginSlug { get; }
    public long BuildId { get; }

    public override string ToString()
    {
        return $"{PluginSlug}/{BuildId}";
    }
}
