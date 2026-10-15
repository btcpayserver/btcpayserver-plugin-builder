using System.ComponentModel.DataAnnotations;

namespace PluginBuilder.ViewModels.Account;

public class TwoFactorStatusViewModel
{
    public bool IsEnabled { get; set; }
    public int RecoveryCodesLeft { get; set; }
    public bool IsRequired { get; set; }
}

public class EnableAuthenticatorViewModel
{
    [Required]
    [StringLength(7, MinimumLength = 6, ErrorMessage = "Enter the 6-digit code from your authenticator app.")]
    [DataType(DataType.Text)]
    [Display(Name = "Verification code")]
    public string Code { get; set; } = string.Empty;

    public string SharedKey { get; set; } = string.Empty;
    public string AuthenticatorUri { get; set; } = string.Empty;
}

public class ShowRecoveryCodesViewModel
{
    public string[] RecoveryCodes { get; set; } = [];
}

public class ConfirmPasswordViewModel
{
    [Required]
    [DataType(DataType.Password)]
    [Display(Name = "Current password")]
    public string Password { get; set; } = string.Empty;
}
