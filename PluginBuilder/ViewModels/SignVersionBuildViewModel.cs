using System.ComponentModel.DataAnnotations;
using PluginBuilder.Components.PluginVersion;

namespace PluginBuilder.ViewModels;

public class SignVersionBuildViewModel
{
    public FullBuildId FullBuildId { get; set; }
    public string PluginSlug { get; set; }
    public PluginVersionViewModel Version { get; set; }
    public string GitRef { get; set; }
    public string Commit { get; set; }
    public string RepositoryLink { get; set; }
    public string ShasumManifest { get; set; }
    public string CreatedDate { get; set; }

    [Required(ErrorMessage = "PGP signature is required")]
    [Display(Name = "PGP Signature")]
    public string Signature { get; set; }
}
