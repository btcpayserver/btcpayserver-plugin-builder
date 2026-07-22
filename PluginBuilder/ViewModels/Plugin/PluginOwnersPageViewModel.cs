namespace PluginBuilder.ViewModels.Plugin;

public class PluginOwnersPageViewModel
{
    public string PluginSlug { get; set; } = string.Empty;
    public string CurrentUserId { get; set; } = string.Empty;
    public bool IsPrimaryOwner { get; set; }
    public List<OwnerVm> Owners { get; set; } = new();
}

public record OwnerVm(string UserId, bool IsPrimary, string? Email, string? AccountDetail, bool EmailConfirmed, string? GithubGistUrl);
