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

    public long? OsmNodeId { get; set; }
    public string? OsmNodeType { get; set; }

    // Required when TagOnOsm=true and OsmNodeId is null (create-new path).
    // Plugin should pass the merchant's coordinates from the BTCPay store
    // address or merchant-supplied input.
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    // Optional. Maps to the OSM amenity= tag. Common values: shop, cafe,
    // restaurant, bar, pub, fast_food. Defaults to "shop" when omitted.
    public string? OsmCategory { get; set; }

    public bool SubmitToDirectory { get; set; }
    public bool TagOnOsm { get; set; }

    // Defaults to true: a BTCPay store accepts on-chain Bitcoin by definition,
    // so currency:XBT=yes is always set. Lightning is per-store configuration,
    // so the plugin must pass the actual store state.
    public bool AcceptsLightning { get; set; } = true;

    // Opt-in un-listing: remove the bitcoin-related tags from an existing OSM
    // element. Requires OsmNodeId + OsmNodeType. Mutually exclusive with TagOnOsm
    // and SubmitToDirectory (v1 scope is OSM-only; directory unlist involves a
    // separate merchant-row/PR/rebuild flow and is out of scope for this endpoint).
    // If the target element no longer carries any of the bitcoin-related tags the
    // service removes, the endpoint returns 409 Conflict.
    public bool UnlistFromOsm { get; set; }

    // Optional structured address. Consumed by the OSM tag writer (addr:*).
    // Each field nullable; only populated keys are written to the node. Plugin
    // is responsible for splitting raw street strings into HouseNumber + Street
    // at the merchant-form boundary.
    public BtcMapsSubmitAddress? Address { get; set; }
}
