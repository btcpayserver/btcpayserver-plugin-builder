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

    // True when the node was created on this request (OsmNodeId was null in
    // the request and the service POSTed /api/0.6/node). Plugin should
    // persist NodeId back to the merchant record so future submissions take
    // the existing-update path.
    public bool Created { get; set; }

    // Populated on an un-list request (UnlistFromOsm=true) with the keys the
    // service actually removed from the element. Null on a tag-on request.
    public string[]? RemovedTags { get; set; }
}
