namespace PluginBuilder.ViewModels.Plugin;

public class PluginReviewViewModel
{
    public long? Id { get; set; }
    public string PluginSlug { get; set; } = default!;
    public long? ReviewerId { get; set; }
    public string? UserId { get; set; }
    public int Rating { get; set; }
    public string? Body { get; set; } = default!;
    public int[]? PluginVersion { get; set; }
    public object? HelpfulVoters { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public record PluginReviewerViewModel(long id, string? user_id, string? username, string? source, string? profile_url, string? avatar_url);
