using System.ComponentModel.DataAnnotations;

namespace PluginBuilder.ViewModels.Account;

public class VerifyGitHubViewModel
{
    [Required]
    [Display(Name = "Public Gist Url")]
    public string GistUrl { get; set; }

    public string Token { get; set; }
}
