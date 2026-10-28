namespace PluginBuilder.ViewModels.Admin;

public class AdminUsersListViewModel : BasePagingViewModel
{
    public List<AdminUsersViewModel> Users { get; set; } = new();
    public string SearchText { get; set; } = null!;

    public override int CurrentPageCount => Users.Count;
}

public class AdminUsersViewModel
{
    public string Id { get; set; } = null!;
    public string Email { get; set; } = null!;
    public string PhoneNumber { get; set; } = null!;
    public bool EmailConfirmed { get; set; }
    public bool LockedOut { get; set; }
    public IList<string> Roles { get; set; } = null!;
    public string? PendingNewEmail { get; set; }
    public long? GithubUserId { get; set; }
    public string? GithubLogin { get; set; }
    public bool GithubVerified { get; set; }
    public string? SignupIp { get; set; }
    public bool AdminLocked { get; set; }
}
