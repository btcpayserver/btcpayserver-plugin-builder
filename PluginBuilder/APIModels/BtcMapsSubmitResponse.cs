namespace PluginBuilder.APIModels;

public sealed class BtcMapsSubmitResponse
{
    public BtcMapsDirectoryResult? Directory { get; set; }
}

public sealed class BtcMapsDirectoryResult
{
    public string? PrUrl { get; set; }
    public int? PrNumber { get; set; }
    public string? Branch { get; set; }
    public string? Skipped { get; set; }
}
