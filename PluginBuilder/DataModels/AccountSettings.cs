using System.ComponentModel.DataAnnotations;
using PluginBuilder.ViewModels;

namespace PluginBuilder.DataModels;

public class AccountSettings
{
    [Display(Name = "Github username")]
    public string? Github { get; set; }

    public NostrSettings? Nostr { get; set; }

    [Display(Name = "Twitter handle")]
    public string? Twitter { get; set; }

    [Display(Name = "Public Email address")]
    public string? Email { get; set; }
    public string? PendingNewEmail { get; set; }
    public PgpKeyViewModel? GPGKey { get; set; }
}

public sealed class NostrSettings
{
    [Display(Name = "Nostr Npub key")]
    public string? Npub { get; set; }
    public string? Proof { get; set; } // eventId (extension) | url/note1/nevent1 (manual)
    public NostrProfileCache? Profile { get; set; }
}

public sealed class NostrProfileCache
{
    public string? PictureUrl { get; set; }
    public string? Name { get; set; }
    public long   LastUpdatedUnix { get; set; }
}
