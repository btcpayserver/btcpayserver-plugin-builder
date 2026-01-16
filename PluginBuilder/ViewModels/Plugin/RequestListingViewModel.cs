using System.ComponentModel.DataAnnotations;

namespace PluginBuilder.ViewModels;

public class RequestListingViewModel
{
    public enum State
    {
        Invalid,
        UpdateOwnerAccountSettings,
        UpdatePluginSettings,
        Done
    }

    public string PluginSlug { get; set; } = string.Empty;

    [MaxLength(200)]
    [Required]
    public string ReleaseNote { get; set; } = string.Empty;

    [Required]
    [Display(Name = "Telegram Verification Message")]
    public string TelegramVerificationMessage { get; set; } = string.Empty;

    [Required]
    [Display(Name = "User Reviews")]
    public string UserReviews { get; set; } = string.Empty;

    public bool PendingListing { get; set; }
    public bool HasPreviousRejection { get; set; }
    public bool CanSendEmailReminder { get; set; }
    public State Step { get; set; }
    public DateTimeOffset? AnnouncementDate { get; set; }
}
