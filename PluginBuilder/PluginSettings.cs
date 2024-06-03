#nullable disable
using System.ComponentModel.DataAnnotations;

namespace PluginBuilder
{
    public class PluginSettings
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
        public List<Tag> PluginTags { get; set; }
    }

    public class Tag
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }
}
