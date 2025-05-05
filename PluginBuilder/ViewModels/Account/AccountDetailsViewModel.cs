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
    public bool GithubAccountVerified { get; set; }
}

public class PgpKey
{
    public string KeyBatchId { get; set; }
    public string KeyUserId { get; set; }
    public string PublicKeyEmailAddress { get; set; }
    public string Fingerprint { get; set; }
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
