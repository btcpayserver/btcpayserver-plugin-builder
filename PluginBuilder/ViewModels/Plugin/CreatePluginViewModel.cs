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

    [Display(Name = "Logo")]
    public IFormFile Logo { get; set; }

    [Display(Name = "Logo")]
    public string LogoUrl { get; set; }
}
