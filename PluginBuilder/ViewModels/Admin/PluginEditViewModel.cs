using PluginBuilder.DataModels;

namespace PluginBuilder.ViewModels.Admin;

public class PluginViewModel
{
    public string Slug { get; set; } = null!;
    public string Identifier { get; set; } = null!;
    public string Settings { get; set; } = null!;
    public PluginVisibilityEnum Visibility { get; set; }
}

public class PluginEditViewModel : PluginViewModel
{
    public List<PluginUsersViewModel> PluginUsers { get; set; }
}

public class PluginUsersViewModel
{
    public string UserId { get; set; } = null!;
    public string Email { get; set; } = null!;
    public bool IsPluginOwner { get; set; }
}
