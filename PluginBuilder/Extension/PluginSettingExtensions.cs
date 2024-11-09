using PluginBuilder.ViewModels;

namespace PluginBuilder.Extension
{
    public static class PluginSettingExtensions
    {
        public static PluginSettings ToPluginSettings(this PluginSettingViewModel viewModel)
        {
            return new PluginSettings
            {
                Description = viewModel.Description,
                Documentation = viewModel.Documentation,
                GitRepository = viewModel.GitRepository,
                GitRef = viewModel.GitRef,
                PluginDirectory = viewModel.PluginDirectory,
                BuildConfig = viewModel.BuildConfig,
                Logo = viewModel.LogoUrl
            };
        }

        public static PluginSettingViewModel ToPluginSettingViewModel(this PluginSettings settings)
        {
            return new PluginSettingViewModel
            {
                Description = settings.Description,
                Documentation = settings.Documentation,
                GitRepository = settings.GitRepository,
                GitRef = settings.GitRef,
                PluginDirectory = settings.PluginDirectory,
                BuildConfig = settings.BuildConfig,
                LogoUrl = settings.Logo
            };
        }
    }
}
