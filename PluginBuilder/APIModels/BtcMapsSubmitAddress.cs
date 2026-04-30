namespace PluginBuilder.APIModels;

// Optional structured address block on BtcMapsSubmitRequest. Populated by the
// plugin when the merchant provides postal-address fields; consumed by
// BtcMapsService to write OSM `addr:*` tags. Each field is optional - the
// service only writes the OSM tags whose corresponding value is populated.
//
// Field ordering follows the OSM `addr:*` convention. HouseNumber + Street are
// kept separate (per OSM) and the plugin is responsible for splitting the raw
// merchant-entered street string into the two components before sending.
public sealed class BtcMapsSubmitAddress
{
    public string? HouseNumber { get; set; }
    public string? Street { get; set; }
    public string? City { get; set; }
    public string? Postcode { get; set; }

    // ISO 3166-1 alpha-2. Validated alongside the top-level Country (which is
    // the directory-submission field) when present; the two are independent.
    public string? Country { get; set; }
}
