using System.ComponentModel.DataAnnotations;

namespace PluginBuilder.ViewModels;

public class RequestListingViewModel
{
    public string PluginSlug { get; set; }
    public string PluginReleaseDescription { get; set; }
    public string TelegramVerificationMessage { get; set; }
    public string UserReviews { get; set; }
    public bool ValidationRequirementMet { get; set; }
    public bool PendingListing { get; set; }
    public bool CanSendEmailReminder { get; set; }

    [Display(Name = "Announcement Date & Time (optional)")]
    public DateTimeOffset? AnnouncementDate { get; set; }
}
