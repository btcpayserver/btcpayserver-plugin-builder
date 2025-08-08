using System.ComponentModel.DataAnnotations;

namespace PluginBuilder.ViewModels;

public class PluginSettingViewModel
{
    [MaxLength(200)]
    [Display(Name = "Documentation link")]
    public string Documentation { get; set; } = null!;

    [MaxLength(200)]
    [Display(Name = "Git repository")]
    public string GitRepository { get; set; } = null!;
    [MaxLength(200)]
    [Display(Name = "Git branch or tag")]
    public string GitRef { get; set; } = null!;

    [MaxLength(200)]
    [Display(Name = "Directory to the plugin's project")]
    public string PluginDirectory { get; set; } = null!;

    [MaxLength(200)]
    [Display(Name = "Dotnet build configuration ")]
    public string BuildConfig { get; set; } = null!;

    [Display(Name = "Logo")]
    public string? LogoUrl { get; set; }

    [Display(Name = "Logo")]
    public IFormFile? Logo { get; set; }
    public bool IsPluginOwner { get; set; }
}
