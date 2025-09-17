namespace PluginBuilder.ViewModels.Plugin;

public class PluginOwnersPageViewModel
{
    public string PluginSlug { get; set; } = string.Empty;
    public string CurrentUserId { get; set; } = string.Empty;

    public bool IsPluginOwner { get; set; }

    public bool IsPrimaryOwner { get; set; }

    public List<OwnerVm> Owners { get; set; } = new();

    public bool CurrentUserIsCoOwner => IsPluginOwner && !IsPrimaryOwner;
    public OwnerVm? PrimaryOwner => Owners.FirstOrDefault(o => o.IsPrimary);

    public IEnumerable<OwnerVm> OwnersPrimaryFirst =>
        Owners.OrderByDescending(o => o.IsPrimary)
            .ThenBy(o => o.Email ?? o.UserId);
}

public record OwnerVm(
    string UserId,
    string? Email,
    bool IsPrimary
);
