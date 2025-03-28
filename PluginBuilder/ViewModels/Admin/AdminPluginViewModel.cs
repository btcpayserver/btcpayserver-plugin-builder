using PluginBuilder.DataModels;

namespace PluginBuilder.ViewModels.Admin;

public class AdminPluginViewModel
{
    public string ProjectSlug { get; set; }
    public string? Version { get; set; }
    public long? BuildId { get; set; }
    public string? BtcPayMinVer { get; set; }
    public bool PreRelease { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string PublisherEmail { get; set; }
    public PluginVisibilityEnum Visibility { get; set; }
}

public class AdminPluginSettingViewModel : BasePagingViewModel
{
    public IEnumerable<AdminPluginViewModel> Plugins { get; set; } = new List<AdminPluginViewModel>();
    public bool VerifiedEmailForPluginPublish { get; set; }
    public string SearchText { get; set; }
    public override int CurrentPageCount => Plugins.Count();
}
