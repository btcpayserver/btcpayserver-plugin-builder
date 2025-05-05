namespace PluginBuilder.ViewModels.Account;

public class PgpKeyViewModel
{
    public string BatchId { get; set; }
    public string Title { get; set; }
    public string KeyUserId { get; set; }
    public string KeyId { get; set; }
    public string Subkeys { get; set; }
    public DateTime? AddedDate { get; set; }
}

public class AddPgpKeyViewModel
{
    public string PublicKey { get; set; }
    public string Title { get; set; }
}
