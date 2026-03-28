#nullable disable
using System.ComponentModel.DataAnnotations;

namespace PluginBuilder.ViewModels.Plugin;

public class CreatePluginViewModel
{
    [Required]
    [Display(Name = "Plugin slug")]
    [MaxLength(30)]
    [MinLength(4)]
    public string PluginSlug { get; set; }

    [Required]
    [Display(Name = "Plugin Title")]
    public string PluginTitle { get; set; }

    [Required]
    [Display(Name = "Plugin description")]
    [MaxLength(500)]
    public string Description { get; set; }

    [Display(Name = "Logo")]
    public IFormFile Logo { get; set; }

    [Display(Name = "Logo")]
    public string LogoUrl { get; set; }

    [Display(Name = "Screenshots")]
    public List<string> ScreenshotsUrl { get; set; } = [];

    [Display(Name = "Screenshots")]
    public List<IFormFile> Screenshots { get; set; } = [];

    [MaxLength(200)]
    [Display(Name = "Plugin Demo Video URL")]
    public string VideoUrl { get; set; }
}
