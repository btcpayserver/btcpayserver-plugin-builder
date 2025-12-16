using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using PluginBuilder.DataModels;
using PluginBuilder.ViewModels.Plugin;

namespace PluginBuilder.ViewModels.Admin;

public class PluginViewModel
{
    public string PluginSlug { get; set; } = null!;
    [ValidateNever]
    public string? Identifier { get; set; }

    [ValidateNever]
    public string? Settings { get; set; }
    public PluginVisibilityEnum Visibility { get; set; }
}

public class PluginEditViewModel : PluginViewModel
{
    [ValidateNever]
    public List<OwnerVm> PluginUsers { get; set; } = new();
    [ValidateNever]
    public PluginSettings PluginSettings { get; set; }

    [Display(Name = "Logo")]
    public IFormFile? LogoFile { get; set; }
}
