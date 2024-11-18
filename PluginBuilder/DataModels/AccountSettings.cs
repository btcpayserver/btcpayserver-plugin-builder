using System.ComponentModel.DataAnnotations;

namespace PluginBuilder.DataModels;

public class AccountSettings
{
    [Display(Name = "Github username")]
    [Required(ErrorMessage = "GitHub profile URL is required.")]
    public string Github { get; set; }

    [Display(Name = "Nostr Npub key")]
    public string? Nostr { get; set; }

    [Display(Name = "Twitter handle")]
    public string? Twitter { get; set; }

    [Display(Name = "Public Email address")]
    public string? Email { get; set; }
}
