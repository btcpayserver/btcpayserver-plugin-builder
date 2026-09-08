namespace PluginBuilder.DataModels;

public static class SettingsKeys
{
    public const string EmailSettings = nameof(EmailSettings);
    public const string VerifiedEmailForPluginPublish = nameof(VerifiedEmailForPluginPublish);
    public const string VerifiedEmailForLogin = nameof(VerifiedEmailForLogin);
    public const string FirstPluginBuildReviewers = nameof(FirstPluginBuildReviewers);
    public const string VerifiedGithub = nameof(VerifiedGithub);
    public const string VerifiedNostr = nameof(VerifiedNostr);
    public const string NostrRelays = nameof(NostrRelays);
    public const string RateLimitPermitLimit = nameof(RateLimitPermitLimit);
    public const string RateLimitWindowSeconds = nameof(RateLimitWindowSeconds);
    public const string RegistrationEnabled = nameof(RegistrationEnabled);
    public const string NewBuildsEnabled = nameof(NewBuildsEnabled);
    public const string NewBuildsWhitelist = nameof(NewBuildsWhitelist);
}
