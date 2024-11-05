namespace PluginBuilder.ViewModels;


public class AccountKeySettingsViewModel
{
    public string PublicKey { get; set; }
    public string Title { get; set; }
}

public class PgpKeyViewModel
{
    public string BatchId { get; set; }
    public string Title { get; set; }
    public string KeyUserId { get; set; }
    public string KeyId { get; set; }
    public string Subkeys { get; set; }
    public DateTime? AddedDate { get; set; }
}

public class PluginApprovalStatusUpdateViewModel
{
    public string PluginSlug { get; set; }
    public string ArmoredMessage { get; set; }
    public string ManifestShasum { get; set; }
}
