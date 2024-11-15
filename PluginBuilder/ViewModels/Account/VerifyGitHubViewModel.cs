using System.ComponentModel.DataAnnotations;

namespace PluginBuilder.ViewModels.Account;

public class VerifyGitHubViewModel
{
    [Required]
    [Display(Name = "Github Profile Url")]
    public string GithubProfileUrl { get; set; }

    [Required]
    [Display(Name = "Public Gist Id")]
    public string GistId { get; set; }

    public string Token { get; set; }
    public bool IsVerified { get; set; }
}
