namespace PluginBuilder.APIModels;

public sealed class BtcMapsSubmitRequest
{
    public string? Name { get; set; }
    public string? Url { get; set; }
    public string? Description { get; set; }

    public string? Type { get; set; }
    public string? SubType { get; set; }
    public string? Country { get; set; }
    public string? Twitter { get; set; }
    public string? Github { get; set; }
    public string? OnionUrl { get; set; }
    public string? Phone { get; set; }

    // BTC Map import RPC fields. Required iff SubmitToBtcMap=true.
    // Plugin captures lat/lon and composes external_id as hostname:storeId
    // so this endpoint just passes through to the btcmap submit_place RPC.
    public double? Lat { get; set; }
    public double? Lon { get; set; }
    public string? Category { get; set; }
    public string? ExternalId { get; set; }

    // Address fields. Optional; forwarded to btcmap as osm:addr:* tags per the
    // BTC Map import-RPC doc's osm:<tag_name> custom-field convention.
    public string? HouseNumber { get; set; }
    public string? Street { get; set; }
    public string? City { get; set; }
    public string? Postcode { get; set; }

    // Routing flags. Default-true preserves the existing call-site semantics
    // for SubmitToDirectory; SubmitToBtcMap defaults false so callers must
    // opt in to the new path.
    public bool SubmitToDirectory { get; set; } = true;
    public bool SubmitToBtcMap { get; set; }
}
