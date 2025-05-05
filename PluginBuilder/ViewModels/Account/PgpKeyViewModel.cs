namespace PluginBuilder.ViewModels.Account;

public class PgpKeyViewModel
{
    public string BatchId { get; set; }
    public string Title { get; set; }
    public string KeyUserId { get; set; }
    public string KeyId { get; set; }
    public string Subkeys { get; set; }
    public DateTime? AddedDate { get; set; }

    /*public string Id { get; set; }
    public string UserId { get; set; }
    public string Title { get; set; }
    public string KeyId { get; set; }
    public string Fingerprint { get; set; }
    public string PublicKey { get; set; }
    public string EmailAddress { get; set; }
    public DateTime CreatedAt { get; set; }*/
}

public class AddPgpKeyViewModel
{
    public string PublicKey { get; set; }
    public string Title { get; set; }
}
