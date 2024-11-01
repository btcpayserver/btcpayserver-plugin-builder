using System.ComponentModel.DataAnnotations;

namespace PluginBuilder
{
    public class AccountSettings
    {
        [Display(Name = "Github username")]
        public string Github { get; set; }

        [Display(Name = "Nostr Npub key")]
        public string? Nostr { get; set; }

        [Display(Name = "Twitter handle")]
        public string? Twitter { get; set; }

        [Display(Name = "Email address")]
        public string? Email { get; set; }
    }
}
