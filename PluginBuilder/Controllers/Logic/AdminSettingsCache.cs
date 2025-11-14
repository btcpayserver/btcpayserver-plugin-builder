using Npgsql;
using PluginBuilder.Util.Extensions;

namespace PluginBuilder.Controllers.Logic;

public class AdminSettingsCache
{
    public bool IsEmailVerificationRequiredForPublish { get; private set; }
    public bool IsEmailVerificationRequiredForLogin { get; private set; }
    public bool IsGithubVerificationRequired { get; private set; }
    public bool IsNostrVerificationRequired { get; private set; }
    public string[] NostrRelays { get; private set; } = Array.Empty<string>();

    public async Task RefreshIsVerifiedEmailRequiredForPublish(NpgsqlConnection conn)
    {
        IsEmailVerificationRequiredForPublish = await conn.GetVerifiedEmailForPluginPublishSetting();
    }

    public async Task RefreshIsVerifiedEmailRequiredForLogin(NpgsqlConnection conn)
    {
        IsEmailVerificationRequiredForLogin = await conn.GetVerifiedEmailForLoginSetting();
    }

    public async Task RefreshAllVerifiedEmailSettings(NpgsqlConnection conn)
    {
        await RefreshIsVerifiedEmailRequiredForPublish(conn);
        await RefreshIsVerifiedEmailRequiredForLogin(conn);
    }

    public async Task RefreshAllAdminSettings(NpgsqlConnection conn)
    {
        await RefreshIsVerifiedEmailRequiredForPublish(conn);
        await RefreshIsVerifiedEmailRequiredForLogin(conn);
        await RefreshIsVerifiedGithubRequired(conn);
        await RefreshNostrVerified(conn);
        await RefreshNostrRelays(conn);
    }

    public async Task RefreshIsVerifiedGithubRequired(NpgsqlConnection conn)
    {
        IsGithubVerificationRequired = await conn.GetVerifiedGithubSetting();
    }

    public async Task RefreshNostrVerified(NpgsqlConnection conn)
    {
        IsNostrVerificationRequired = await conn.GetVerifiedNostrSetting();
    }

    public async Task RefreshNostrRelays(NpgsqlConnection conn)
    {
        NostrRelays = await conn.GetNostrRelaysSetting();
    }
}
