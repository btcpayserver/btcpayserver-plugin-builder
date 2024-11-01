using Microsoft.AspNetCore.Identity;

namespace PluginBuilder.ViewModels.Admin;

public class EditUserRolesViewModel
{
    public string UserId { get; set; }
    public string UserName { get; set; }
    public IList<string> UserRoles { get; set; }
    public List<IdentityRole> AvailableRoles { get; set; }
}

