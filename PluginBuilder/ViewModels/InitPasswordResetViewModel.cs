#nullable disable
using System.ComponentModel.DataAnnotations;

namespace PluginBuilder.ViewModels;

public class InitPasswordResetViewModel
{
    [Required]
    [EmailAddress]
    [Display(Name = "Email")]
    public string Email { get; set; }

    public string PasswordResetToken { get; set; }
}


public class UserChangeEmailViewModel
{
    [Required]
    [EmailAddress]
    [Display(Name = "Old Email")]
    public string OldEmail { get; set; }

    [Required]
    [EmailAddress]
    [Display(Name = "New Email")]
    public string NewEmail { get; set; }
    public string PendingNewEmail { get; set; }
}
