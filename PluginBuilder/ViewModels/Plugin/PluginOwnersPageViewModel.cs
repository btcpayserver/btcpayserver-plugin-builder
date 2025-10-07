using System.Text.Json;

namespace PluginBuilder.ViewModels.Plugin;

public class PluginOwnersPageViewModel
{
    public string PluginSlug { get; set; } = string.Empty;
    public string CurrentUserId { get; set; } = string.Empty;
    public bool IsPrimaryOwner { get; set; }
    public List<OwnerVm> Owners { get; set; } = new();
    public OwnerVm? PrimaryOwner => Owners.FirstOrDefault(o => o.IsPrimary);
}

public record OwnerVm(
    string UserId,
    bool IsPrimary,
    string? Email,
    string? AccountDetail
);

public record SignatureProofResponse(bool valid, string message, SignatureProof? proof = null);
public class SignatureProof
{
    public string Armour { get; init; }
    public string KeyId { get; init; }
    public string Fingerprint { get; init; }
    public DateTime SignedAt { get; init; }
    public DateTimeOffset VerifiedAt { get; init; }
}
