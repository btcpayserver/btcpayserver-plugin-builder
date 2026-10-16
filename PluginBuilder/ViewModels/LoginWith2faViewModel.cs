#nullable disable
using System.ComponentModel.DataAnnotations;

namespace PluginBuilder.ViewModels;

public class LoginWith2faViewModel
{
    [Required]
    [StringLength(7, MinimumLength = 6, ErrorMessage = "Enter the 6-digit code from your authenticator app.")]
    [DataType(DataType.Text)]
    [Display(Name = "Authenticator code")]
    public string TwoFactorCode { get; set; }

    [Display(Name = "Remember this browser")]
    public bool RememberMachine { get; set; }

    public bool RememberMe { get; set; }
}

public class LoginWithRecoveryCodeViewModel
{
    [Required]
    [DataType(DataType.Text)]
    [Display(Name = "Recovery code")]
    public string RecoveryCode { get; set; }
}
