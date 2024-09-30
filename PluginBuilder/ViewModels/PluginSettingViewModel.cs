using System.ComponentModel.DataAnnotations;

namespace PluginBuilder.ViewModels
{
    public class PluginSettingViewModel
    {
        [MaxLength(200)]
        [Display(Name = "Documentation link")]
        public string Documentation { get; set; }

        [MaxLength(200)]
        [Display(Name = "Git repository")]
        public string GitRepository { get; set; }
        [MaxLength(200)]
        [Display(Name = "Git branch or tag")]
        public string GitRef { get; set; }

        [MaxLength(200)]
        [Display(Name = "Directory to the plugin's project")]
        public string PluginDirectory { get; set; }

        [MaxLength(200)]
        [Display(Name = "Dotnet build configuration ")]
        public string BuildConfig { get; set; }

        [Display(Name = "Plugin description")]
        public string Description { get; set; }

        [Display(Name = "Logo")]
        public string LogoUrl { get; set; }

        [Display(Name = "Logo")]
        public IFormFile Logo { get; set; }

    }
}
