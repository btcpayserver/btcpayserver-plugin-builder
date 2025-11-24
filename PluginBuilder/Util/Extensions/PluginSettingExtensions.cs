using PluginBuilder.DataModels;
using PluginBuilder.ViewModels;
using PluginBuilder.ViewModels.Admin;
using PluginBuilder.ViewModels.Plugin;

namespace PluginBuilder.Util.Extensions;

public static class PluginSettingExtensions
{
    public static PluginSettings ToPluginSettings(this PluginSettingViewModel viewModel)
    {
        return new PluginSettings
        {
            PluginTitle = viewModel.PluginTitle,
            Description = viewModel.Description,
            Documentation = viewModel.Documentation,
            GitRepository = viewModel.GitRepository,
            GitRef = viewModel.GitRef,
            PluginDirectory = viewModel.PluginDirectory,
            BuildConfig = viewModel.BuildConfig,
            Logo = viewModel.LogoUrl,
            RequireGPGSignatureForRelease = viewModel.RequireGPGSignatureForRelease,
        };
    }

    public static PluginSettingViewModel ToPluginSettingViewModel(this PluginSettings settings)
    {
        return new PluginSettingViewModel
        {
            PluginTitle = settings.PluginTitle,
            Description = settings.Description,
            Documentation = settings.Documentation,
            GitRepository = settings.GitRepository,
            GitRef = settings.GitRef,
            PluginDirectory = settings.PluginDirectory,
            BuildConfig = settings.BuildConfig,
            LogoUrl = settings.Logo,
            RequireGPGSignatureForRelease = settings.RequireGPGSignatureForRelease
        };
    }

    public static ImportReviewViewModel UpdatePluginReviewerData(this ImportReviewViewModel model, AccountSettings settings)
    {
        if (!string.IsNullOrEmpty(settings.Github))
        {
            var githubUserName = settings.Github.Trim().TrimStart('@').Trim('/');
            model.ReviewerName = githubUserName;
            model.ReviewerProfileUrl = $"https://github.com/{githubUserName}";
            model.ReviewerAvatarUrl = $"https://avatars.githubusercontent.com/{githubUserName}";
        }
        else if (settings.Nostr != null && !string.IsNullOrEmpty(settings.Nostr.Npub))
        {
            var nostr = settings.Nostr;
            model.ReviewerName = string.IsNullOrWhiteSpace(nostr.Profile?.Name) ? $"{nostr.Npub[..8]}…" : nostr.Profile.Name;
            model.ReviewerProfileUrl = $"https://primal.net/p/{nostr.Npub}";
            model.ReviewerAvatarUrl = !string.IsNullOrWhiteSpace(nostr.Profile?.PictureUrl) && Uri.TryCreate(nostr.Profile.PictureUrl, UriKind.Absolute, out var avatarUri) &&
                                    (avatarUri.Scheme == Uri.UriSchemeHttp || avatarUri.Scheme == Uri.UriSchemeHttps) ? nostr.Profile.PictureUrl : null;
        }
        return model;
    }
}
