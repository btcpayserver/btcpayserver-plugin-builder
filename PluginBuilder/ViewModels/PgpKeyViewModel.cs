namespace PluginBuilder.ViewModels;

public class PgpKeyViewModel
{
    public string? KeyId { get; set; }
    public string? Fingerprint { get; set; }
    public string? PublicKey { get; set; }
    public DateTimeOffset CreatedDate { get; set; }
    public DateTimeOffset AddedDate { get; set; }
    public long ValidDays { get; set; }
    public int Version { get; set; }
}

public record SignatureProofResponse(bool valid, string message, SignatureProof? proof = null);

public record UserKey(string PublicKeyArmored, string Fingerprint);

public class SignatureProof
{
    public string? Armour { get; set; }
    public string? KeyId { get; set; }
    public string? Fingerprint { get; set; }
    public DateTime SignedAt { get; set; }
    public DateTimeOffset VerifiedAt { get; set; }
}
