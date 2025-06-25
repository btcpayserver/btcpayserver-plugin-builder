using System.ComponentModel.DataAnnotations;

namespace PluginBuilder.ViewModels.Home;

public class ConfirmEmailViewModel
{
    public bool EmailConfirmed { get; set; }
    
    [Required]
    [EmailAddress]
    [Display(Name = "Email address")]
    public string Email { get; set; } = null!;
}
