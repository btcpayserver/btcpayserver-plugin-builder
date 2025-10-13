using PluginBuilder.ViewModels;

namespace PluginBuilder.Util.Extensions;

public static class PluginSettingExtensions
{
    public static PluginSettings ToPluginSettings(this PluginSettingViewModel viewModel)
    {
        return new PluginSettings
        {
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
            Documentation = settings.Documentation,
            GitRepository = settings.GitRepository,
            GitRef = settings.GitRef,
            PluginDirectory = settings.PluginDirectory,
            BuildConfig = settings.BuildConfig,
            LogoUrl = settings.Logo,
            RequireGPGSignatureForRelease = settings.RequireGPGSignatureForRelease
        };
    }
}
