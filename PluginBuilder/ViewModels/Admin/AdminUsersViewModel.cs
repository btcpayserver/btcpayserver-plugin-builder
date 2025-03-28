namespace PluginBuilder.ViewModels.Admin;

public class AdminUsersListViewModel : BasePagingViewModel
{
    public List<AdminUsersViewModel> Users { get; set; } = new();
    public string SearchText { get; set; }
    public override int CurrentPageCount => Users.Count;
}

public class AdminUsersViewModel
{
    public string Id { get; set; }
    public string Email { get; set; }
    public string UserName { get; set; }
    public string PhoneNumber { get; set; }
    public bool EmailConfirmed { get; set; }
    public DateTimeOffset? LockoutEnd { get; set; }
    public IList<string> Roles { get; set; } // List of roles
}
