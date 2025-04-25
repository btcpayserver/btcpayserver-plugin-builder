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
