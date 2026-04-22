namespace PluginBuilder.APIModels;

public sealed class BtcMapsSubmitResponse
{
    public BtcMapsDirectoryResult? Directory { get; set; }
    public BtcMapsOsmResult? Osm { get; set; }
}

public sealed class BtcMapsDirectoryResult
{
    public string? PrUrl { get; set; }
    public int? PrNumber { get; set; }
    public string? Branch { get; set; }
    public string? Skipped { get; set; }
}

public sealed class BtcMapsOsmResult
{
    public long? ChangesetId { get; set; }
    public long? NodeId { get; set; }
    public string? NodeType { get; set; }
    public int? NewVersion { get; set; }
    public string? Skipped { get; set; }
}
