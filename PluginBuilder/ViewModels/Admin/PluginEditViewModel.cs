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
    public string ActiveTab { get; set; } = PluginEditTabs.Settings;

    [ValidateNever]
    public ImportReviewViewModel ImportReview { get; set; } = new();

    [ValidateNever]
    public string? OpenCompatibilityVersion { get; set; }

    [ValidateNever]
    public List<OwnerVm> PluginUsers { get; set; } = new();

    [ValidateNever]
    public List<PublishedPluginVersionAdminViewModel> PublishedVersions { get; set; } = new();

    [ValidateNever]
    public PluginSettings PluginSettings { get; set; } = new();

    [Display(Name = "Logo")]
    public IFormFile? LogoFile { get; set; }

    [Display(Name = "Images")]
    public List<IFormFile> Images { get; set; } = [];
}

public static class PluginEditTabs
{
    public const string Settings = "settings";
    public const string Owners = "owners";
    public const string Versions = "versions";
    public const string Reviews = "reviews";

    public static string Normalize(string? tab)
    {
        return tab?.ToLowerInvariant() switch
        {
            Owners => Owners,
            Versions => Versions,
            Reviews => Reviews,
            _ => Settings
        };
    }
}

public class PublishedPluginVersionAdminViewModel
{
    public string Version { get; set; } = null!;
    public string CompatibilityModalId => $"btcpay-compatibility-modal-{Version.Replace('.', '-')}";
    public string BtcPayMinVersion { get; set; } = null!;
    public bool HasBtcPayMinVersionOverride { get; set; }
    public string? BtcPayMaxVersion { get; set; }
    public bool HasBtcPayMaxVersionOverride { get; set; }
    public bool PreRelease { get; set; }
    public string? ManifestCondition { get; set; }
}
