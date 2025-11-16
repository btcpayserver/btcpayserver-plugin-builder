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
    public string PluginSlug { get; set; }
    public string ReleaseNote { get; set; }

    [Display(Name = "Telegram Verification Message")]
    public string TelegramVerificationMessage { get; set; }

    [Display(Name = "User Reviews")]
    public string UserReviews { get; set; }
    public bool PendingListing { get; set; }
    public bool CanSendEmailReminder { get; set; }
    public State Step { get; set; }
    public DateTimeOffset? AnnouncementDate { get; set; }
}
