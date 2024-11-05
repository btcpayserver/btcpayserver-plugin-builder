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
        public List<PgpKey> PgpKeys { get; set; }
    }

    public class PgpKey
    {
        public string KeyBatchId { get; set; }
        public string KeyUserId { get; set; }
        public string Title { get; set; }
        public string PublicKey { get; set; }
        public string KeyId { get; set; }
        public int BitStrength { get; set; }
        public bool IsMasterKey { get; set; }
        public bool IsEncryptionKey { get; set; }
        public string Algorithm { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime AddedDate { get; set; }
        public long ValidDays { get; set; }
        public int Version { get; set; }
    }
}
