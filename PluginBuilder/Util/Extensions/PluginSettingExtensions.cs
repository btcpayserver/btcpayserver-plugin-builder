using PluginBuilder.DataModels;
using PluginBuilder.Services;
using PluginBuilder.ViewModels;
using PluginBuilder.ViewModels.Admin;

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
            var safe = Uri.EscapeDataString(githubUserName);

            model.ReviewerName = githubUserName;
            model.ReviewerProfileUrl = $"{ExternalProfileUrls.GithubBaseUrl}{safe}";
            model.ReviewerAvatarUrl = string.Format(ExternalProfileUrls.GithubAvatarFormat, safe, 48);
        }
        else if (settings.Nostr != null && !string.IsNullOrEmpty(settings.Nostr.Npub))
        {
            var nostr = settings.Nostr;
            model.ReviewerName = string.IsNullOrWhiteSpace(nostr.Profile?.Name) ? $"{nostr.Npub[..8]}…" : nostr.Profile.Name;
            model.ReviewerProfileUrl = string.Format(ExternalProfileUrls.PrimalProfileFormat, Uri.EscapeDataString(nostr.Npub));
            model.ReviewerAvatarUrl = !string.IsNullOrWhiteSpace(nostr.Profile?.PictureUrl) && Uri.TryCreate(nostr.Profile.PictureUrl, UriKind.Absolute, out var avatarUri) &&
                                      (avatarUri.Scheme == Uri.UriSchemeHttp || avatarUri.Scheme == Uri.UriSchemeHttps) ? nostr.Profile.PictureUrl : null;
        }
        return model;
    }
}
