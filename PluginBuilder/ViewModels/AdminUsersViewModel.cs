namespace PluginBuilder.ViewModels;

public class AdminUsersViewModel
{
    public string Email { get; set; }
    public string UserName { get; set; }
    public string PhoneNumber { get; set; }
    public bool EmailConfirmed { get; set; }
    public DateTimeOffset? LockoutEnd { get; set; }
    public IList<string> Roles { get; set; } // List of roles
}
