using System.ComponentModel.DataAnnotations;

namespace PluginBuilder.ViewModels.Home;

public class ForgotPasswordViewModel
{
    [Required]
    [EmailAddress]
    [Display(Name = "Email address")]
    public string Email { get; set; } = null!;

    public bool FormSubmitted { get; set; }
}
