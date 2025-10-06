
namespace PluginBuilder.ViewModels.Home;

public sealed class PluginDetailsViewModel : BasePagingViewModel
{

    public PluginDetailsViewModel()
    {
        Count = 10;
    }
    public APIModels.PublishedPlugin Plugin { get; init; } = new();
    public string Sort { get; init; } = "newest";
    public IReadOnlyList<ReviewItem> Items { get; init; } = Array.Empty<ReviewItem>();
    public override int CurrentPageCount => Items.Count;
}

public class ReviewItem
{
    public long Id { get; set; }
    public string? AuthorUrl { get; set; }
    public string AuthorDisplay { get; set; } = "Anonymous";
    public int Rating { get; set; }
    public string? Body { get; set; }
    public string? PluginVersion { get; set; }
    public DateTime CreatedAt { get; set; }
    public long HelpfulCount { get; set; }
    public bool CanEdit { get; set; }
    public bool? UserVoteHelpful { get; set; }
}
