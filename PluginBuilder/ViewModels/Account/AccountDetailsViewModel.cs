using System.ComponentModel.DataAnnotations;
using PluginBuilder.DataModels;

namespace PluginBuilder.ViewModels.Account;

public class AccountDetailsViewModel
{
    [Display(Name = "Account Email")]
    public string AccountEmail { get; set; }
    public bool AccountEmailConfirmed { get; set; }
    public bool NeedToVerifyEmail { get; set; }
    public AccountSettings Settings { get; set; }
}
