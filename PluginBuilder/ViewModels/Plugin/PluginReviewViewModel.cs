namespace PluginBuilder.ViewModels.Plugin;

public class PluginReviewViewModel
{
    public long? Id { get; set; }
    public string PluginSlug { get; set; } = default!;
    public string? UserId { get; set; }
    public int Rating { get; set; }
    public string? Body { get; set; } = default!;
    public int[]? PluginVersion { get; set; }
    public object? HelpfulVoters { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? AuthorName { get; set; }
    public string? AuthorProfileUrl { get; set; }
    public string? AuthorAvatarUrl { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
