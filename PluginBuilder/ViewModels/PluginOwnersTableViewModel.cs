using PluginBuilder.ViewModels.Plugin;

namespace PluginBuilder.ViewModels;

public sealed class PluginOwnersTableViewModel(PluginSlug pluginSlug)
{
    public string Controller { get; init; } = "Plugin";
    public PluginSlug PluginSlug { get; init; } = pluginSlug;
    public IReadOnlyList<OwnerVm> Owners { get; init; } = Array.Empty<OwnerVm>();
    public string? CurrentUserId { get; init; }

    public bool CanAddOwner { get; init; }
    public bool CanManageOwners { get; init; }
    public bool CanLeave { get; init; }
}
