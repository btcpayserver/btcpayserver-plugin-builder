#nullable disable
using System.ComponentModel.DataAnnotations;

namespace PluginBuilder;

public class PluginSettings
{
    [MaxLength(200)]
    [Display(Name = "Documentation link")]
    public string Documentation { get; set; }

    [MaxLength(200)]
    [Display(Name = "Git repository")]
    public string GitRepository { get; set; }

    [MaxLength(200)]
    [Display(Name = "Git branch or tag")]
    public string GitRef { get; set; }

    [MaxLength(200)]
    [Display(Name = "Directory to the plugin's project")]
    public string PluginDirectory { get; set; }

    [MaxLength(200)]
    [Display(Name = "Dotnet build configuration ")]
    public string BuildConfig { get; set; }
    public string PluginTitle { get; set; }
    public string Description { get; set; }
    public string Logo { get; set; }
    public PluginRequestListingRecord RequestListing { get; set; }
}

public class PluginRequestListingRecord
{
    public string ReleaseNote { get; set; }
    public string TelegramVerificationMessage { get; set; }
    public string UserReviews { get; set; }
    public DateTimeOffset DateAdded { get; set; }
    public DateTimeOffset LastReminderEmailSent { get; set; }
    public DateTimeOffset? AnnouncementDate { get; set; }
}
