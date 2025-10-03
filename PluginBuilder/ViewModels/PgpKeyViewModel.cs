namespace PluginBuilder.ViewModels;

public class PgpKeyViewModel
{
    public string KeyId { get; set; }
    public string Fingerprint { get; set; }
    public string PublicKey { get; set; }
    public DateTimeOffset CreatedDate { get; set; }
    public DateTimeOffset AddedDate { get; set; }
    public long ValidDays { get; set; }
    public int Version { get; set; }
}
