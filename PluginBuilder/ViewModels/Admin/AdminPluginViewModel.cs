namespace PluginBuilder.ViewModels.Admin;

public class AdminPluginViewModel
{
    public string ProjectSlug { get; set; }
    public string Version { get; set; }
    public long BuildId { get; set; }
    public string BtccpayMinVer { get; set; }
    public bool PreRelease { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string PublisherEmail { get; set; }
    public Dictionary<string, List<BuildListViewModel.BuildViewModel>> PluginSlugBuilds { get; set; } = new();
}
