using PluginBuilder.DataModels;

namespace PluginBuilder.ViewModels.Account;

public class AccountDetailsViewModel
{
    public string AccountEmail { get; set; }
    public AccountSettings Settings { get; set; }
}
