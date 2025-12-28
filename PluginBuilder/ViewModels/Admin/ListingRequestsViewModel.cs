using PluginBuilder.DataModels;

namespace PluginBuilder.ViewModels.Admin;

public class ListingRequestsViewModel
{
    public List<ListingRequestItemViewModel> Requests { get; set; } = new();
    public string? StatusFilter { get; set; }
}

public class ListingRequestItemViewModel
{
    public int Id { get; set; }
    public string PluginSlug { get; set; } = null!;
    public string? PluginTitle { get; set; }
    public PluginListingRequestStatus Status { get; set; }
    public DateTimeOffset SubmittedAt { get; set; }
    public string PrimaryOwnerEmail { get; set; } = null!;
    public DateTimeOffset? AnnouncementDate { get; set; }
}

public class ListingRequestDetailViewModel
{
    public int Id { get; set; }
    public string PluginSlug { get; set; } = null!;
    public string? PluginTitle { get; set; }
    public string? PluginDescription { get; set; }
    public string? Logo { get; set; }
    public string? GitRepository { get; set; }
    public string? Documentation { get; set; }
    public string ReleaseNote { get; set; } = null!;
    public string TelegramVerificationMessage { get; set; } = null!;
    public string UserReviews { get; set; } = null!;
    public DateTimeOffset? AnnouncementDate { get; set; }
    public PluginListingRequestStatus Status { get; set; }
    public DateTimeOffset SubmittedAt { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? ReviewedByEmail { get; set; }
    public string? RejectionReason { get; set; }
    public List<OwnerVerificationViewModel> Owners { get; set; } = new();
    public string PrimaryOwnerEmail { get; set; } = null!;
}

public class OwnerVerificationViewModel
{
    public string? Email { get; set; }
    public bool EmailVerified { get; set; }
    public bool IsPrimary { get; set; }
    public string? NostrProfile { get; set; }
    public string? GithubProfile { get; set; }
}

public class ApproveListingRequestViewModel
{
    public int RequestId { get; set; }
}

public class RejectListingRequestViewModel
{
    public int RequestId { get; set; }
    public string RejectionReason { get; set; } = null!;
}
