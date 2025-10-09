
using PluginBuilder.APIModels;

namespace PluginBuilder.ViewModels.Home;

public sealed class PluginDetailsViewModel : BasePagingViewModel
{

    public PluginDetailsViewModel()
    {
        Count = 10;
    }
    public PublishedPlugin Plugin { get; init; } = new();
    public string Sort { get; set; } = "newest";
    public IReadOnlyList<ReviewItem> Items { get; init; } = Array.Empty<ReviewItem>();
    public override int CurrentPageCount => Items.Count;
    public bool IsAdmin { get; set; }
    public bool? IsOwner { get; set; }
    public List<string>? PluginVersions { get; set; }
    public bool ShowHiddenNotice { get; set; }
    public List<GitHubContributor> Contributors { get; init; } = new();
    public int? RatingFilter  { get; set; }
}

public class ReviewItem
{
    public long Id { get; set; }
    public string? AuthorUrl { get; set; }
    public string AuthorDisplay { get; set; } = "Anonymous";
    public string? AuthorAvatarUrl { get; set; }
    public int Rating { get; set; }
    public string? Body { get; set; }
    public string? PluginVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public bool IsReviewOwner { get; set; }
    public int UpCount { get; set; }
    public int DownCount { get; set; }
    public bool? UserVoteHelpful { get; set; }
}
