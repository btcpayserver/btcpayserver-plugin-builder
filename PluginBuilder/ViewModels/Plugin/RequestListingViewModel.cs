using System.ComponentModel.DataAnnotations;

namespace PluginBuilder.ViewModels;

public class RequestListingViewModel
{
    public string PluginSlug { get; set; } = string.Empty;

    [MaxLength(200)]
    [Required(ErrorMessage = "Release note is required.")]
    public string ReleaseNote { get; set; } = string.Empty;

    [Required(ErrorMessage = "Telegram verification message is required.")]
    [Display(Name = "Telegram Verification Message")]
    public string TelegramVerificationMessage { get; set; } = string.Empty;

    [Required(ErrorMessage = "User reviews are required.")]
    [Display(Name = "User Reviews")]
    public string UserReviews { get; set; } = string.Empty;
    public bool HasRequests { get; set; }
    public bool PendingListing { get; set; }
    public bool HasPreviousRejection { get; set; }
    public bool CanSendEmailReminder { get; set; }
    public DateTimeOffset? AnnouncementDate { get; set; }

    public bool HasDescription { get; set; }
    public bool HasGitRepository { get; set; }
    public bool HasDocumentation { get; set; }
    public bool HasVideoUrl { get; set; }
    public bool HasLogo { get; set; }
    public List<RequestListingOwnerViewModel> Owners { get; set; } = [];

    public bool PluginSettingsComplete =>
        HasDescription && HasGitRepository && HasDocumentation && HasVideoUrl && HasLogo;

    public bool OwnerAccountsComplete => Owners.Count > 0 && Owners.All(owner => owner.Complete);
    public bool ListingFormComplete =>
        !string.IsNullOrWhiteSpace(ReleaseNote) &&
        IsValidTelegramVerificationMessage(TelegramVerificationMessage) &&
        !string.IsNullOrWhiteSpace(UserReviews);
    public bool CanSubmit => !PendingListing && PluginSettingsComplete && OwnerAccountsComplete && ListingFormComplete;

    public static bool IsValidTelegramVerificationMessage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var telegramUri))
            return false;

        var segments = telegramUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return telegramUri.Scheme == Uri.UriSchemeHttps &&
               telegramUri.Host.Equals("t.me", StringComparison.OrdinalIgnoreCase) &&
               segments.Length >= 2 &&
               segments[0].Equals("btcpayserver", StringComparison.OrdinalIgnoreCase);
    }
}

public class RequestListingOwnerViewModel
{
    public string UserId { get; set; } = string.Empty;
    public string? Email { get; set; }
    public bool IsPrimary { get; set; }
    public bool IsCurrentUser { get; set; }
    public bool EmailVerified { get; set; }
    public bool GithubVerified { get; set; }
    public bool NostrVerified { get; set; }
    public bool Complete => EmailVerified && GithubVerified && NostrVerified;
}
