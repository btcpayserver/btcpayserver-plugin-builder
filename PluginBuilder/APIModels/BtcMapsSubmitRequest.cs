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
}
