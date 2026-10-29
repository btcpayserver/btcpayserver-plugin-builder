namespace PluginBuilder.ViewModels.Admin;

public class AdminModerationViewModel : BasePagingViewModel
{
    public List<AdminBanViewModel> Bans { get; set; } = [];
    public override int CurrentPageCount => Bans.Count;
}

public class AdminBanViewModel
{
    public string Kind { get; set; } = "";
    public long Id { get; set; }
    public string Target { get; set; } = "";
    public long? GithubUserId { get; set; }
    public string Reason { get; set; } = "";
    public string? BannedBy { get; set; }
    public DateTimeOffset BannedAt { get; set; }
}
