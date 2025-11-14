using PluginBuilder.Util.Enums;

namespace PluginBuilder.ViewModels.Admin;

public class ImportReviewViewModel
{
    public string PluginSlug { get; set; }
    public ImportReviewSourceEnum Source { get; set; } = ImportReviewSourceEnum.Nostr;
    public string Handle { get; set; }
    public string ProfileUrl { get; set; }
    public string SourceUrl { get; set; }
    public string Review { get; set; }
    public int Stars { get; set; } = 5;
}
