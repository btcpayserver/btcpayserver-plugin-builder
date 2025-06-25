using Microsoft.AspNetCore.Identity;

namespace PluginBuilder.ViewModels.Admin;

public class EditUserRolesViewModel
{
    public string UserId { get; set; } = null!;
    public string UserName { get; set; } = null!;
    public IList<string> UserRoles { get; set; } = null!;
    public List<IdentityRole> AvailableRoles { get; set; } = null!;
}
