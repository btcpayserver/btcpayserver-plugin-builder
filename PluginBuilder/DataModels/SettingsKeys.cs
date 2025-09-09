namespace PluginBuilder.DataModels;

// TODO: Store all references to settings keys in single place
public static class SettingsKeys
{
    public const string EmailSettings = nameof(EmailSettings);
    public const string VerifiedEmailForPluginPublish = nameof(VerifiedEmailForPluginPublish);
    public const string VerifiedEmailForLogin = nameof(VerifiedEmailForLogin);
    public const string FirstPluginBuildReviewers = nameof(FirstPluginBuildReviewers);
}
