using System.ComponentModel.DataAnnotations;
using PluginBuilder.ViewModels;

namespace PluginBuilder.DataModels;

public class AccountSettings
{
    [Display(Name = "Github username")]
    public string? Github { get; set; }

    [Display(Name = "Nostr Npub key")]
    public string? Nostr { get; set; }

    [Display(Name = "Twitter handle")]
    public string? Twitter { get; set; }

    [Display(Name = "Public Email address")]
    public string? Email { get; set; }
    public string? PendingNewEmail { get; set; }
    public PgpKeyViewModel? GPGKey { get; set; }
}
