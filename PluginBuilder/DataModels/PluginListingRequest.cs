namespace PluginBuilder.DataModels;

public class PluginListingRequest
{
    public int Id { get; set; }
    public string PluginSlug { get; set; } = null!;
    public string ReleaseNote { get; set; } = null!;
    public string TelegramVerificationMessage { get; set; } = null!;
    public string UserReviews { get; set; } = null!;
    public string? ReviewerFeedback { get; set; }
    public DateTimeOffset? AnnouncementDate { get; set; }
    public PluginListingRequestStatus Status { get; set; }
    public DateTimeOffset SubmittedAt { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? ReviewedBy { get; set; }
    public string? RejectionReason { get; set; }
}

public class ListingHistoryViewModel
{
    public string PluginSlug { get; set; } = null!;
    public string? PluginTitle { get; set; }
    public List<ListingHistoryItemViewModel> Requests { get; set; } = new();
}

public class ListingHistoryItemViewModel
{
    public int Id { get; set; }
    public PluginListingRequestStatus Status { get; set; }
    public string ReleaseNote { get; set; } = null!;
    public DateTimeOffset SubmittedAt { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? RejectionReason { get; set; }
    public string? ReviewerFeedback { get; set; }
}

public enum PluginListingRequestStatus
{
    Pending,
    Approved,
    Rejected
}
