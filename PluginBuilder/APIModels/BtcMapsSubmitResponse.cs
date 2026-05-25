namespace PluginBuilder.APIModels;

public sealed class BtcMapsSubmitResponse
{
    public BtcMapsDirectoryResult? Directory { get; set; }
    public BtcMapsBtcMapResult? BtcMap { get; set; }
}

public sealed class BtcMapsDirectoryResult
{
    public string? PrUrl { get; set; }
    public int? PrNumber { get; set; }
    public string? Branch { get; set; }
    public string? Skipped { get; set; }
}

public sealed class BtcMapsBtcMapResult
{
    public long? Id { get; set; }
    public string? Origin { get; set; }
    public string? ExternalId { get; set; }

    // BTC Map treats import-RPC submissions as queued places that go through
    // their reviewer workflow before appearing on the public directory map.
    // A successful submit_place call means "submission accepted for review",
    // not "place is live." Constant for clients to key off.
    public string Status { get; set; } = "submitted-for-review";
}
