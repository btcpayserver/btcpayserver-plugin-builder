using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using PluginBuilder.DataModels;

namespace PluginBuilder.ViewModels.Admin;

public class PluginViewModel
{
    public string PluginSlug { get; set; } = null!;
    [ValidateNever]
    public string Identifier { get; set; } = null!;

    [ValidateNever]
    public string Settings { get; set; } = null!;
    public PluginVisibilityEnum Visibility { get; set; }
}

public class PluginEditViewModel : PluginViewModel
{
    [ValidateNever]
    public List<PluginUsersViewModel> PluginUsers { get; set; }
    [ValidateNever]
    public PluginSettings PluginSettings { get; set; }

    [Display(Name = "Logo")]
    public IFormFile? LogoFile { get; set; }
}

public class PluginUsersViewModel
{
    public string UserId { get; set; } = null!;
    public string Email { get; set; } = null!;
    public bool IsPluginOwner { get; set; }
}
