#nullable disable
using System.ComponentModel.DataAnnotations;
using PluginBuilder.Views.Enums;

namespace PluginBuilder.ViewModels
{
    public class CreatePluginViewModel
    {
        [Required]
        [Display(Name = "Plugin slug")]
        [MaxLength(30)]
        [MinLength(4)]
        public string PluginSlug { get; set; }
    }
}
