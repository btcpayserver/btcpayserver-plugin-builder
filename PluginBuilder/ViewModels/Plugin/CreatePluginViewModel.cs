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
}
