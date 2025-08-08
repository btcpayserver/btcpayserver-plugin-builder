using Dapper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Npgsql;
using PluginBuilder.DataModels;
using PluginBuilder.Services;

namespace PluginBuilder.Util.Extensions;

public static class NpgsqlConnectionExtensions
{
    public static async Task<PluginSettings?> GetSettings(this NpgsqlConnection connection, PluginSlug pluginSlug)
    {
        var r = await connection.QueryFirstOrDefaultAsync<string>("SELECT settings FROM plugins WHERE slug=@pluginSlug",
            new { pluginSlug = pluginSlug.ToString() });
        if (r is null)
            return null;
        return JsonConvert.DeserializeObject<PluginSettings>(r, CamelCaseSerializerSettings.Instance);
    }

    public static async Task<bool> SetPluginSettings(this NpgsqlConnection connection, PluginSlug pluginSlug, PluginSettings pluginSettings)
    {
        var count = await connection.ExecuteAsync("UPDATE plugins SET settings=@settings::JSONB WHERE slug=@pluginSlug",
            new { pluginSlug = pluginSlug.ToString(), settings = JsonConvert.SerializeObject(pluginSettings, CamelCaseSerializerSettings.Instance) });
        return count == 1;
    }

    public static async Task SetAccountDetailSettings(this NpgsqlConnection connection, AccountSettings accountSettings, string userId)
    {
        await connection.ExecuteAsync(
            "UPDATE \"AspNetUsers\" SET \"AccountDetail\" = @settings::JSONB WHERE \"Id\" = @userId",
            new { userId, settings = JsonConvert.SerializeObject(accountSettings, CamelCaseSerializerSettings.Instance) }
        );
    }

    public static async Task<AccountSettings?> GetAccountDetailSettings(this NpgsqlConnection connection, string userId)
    {
        var accountDetail = await connection.QueryFirstOrDefaultAsync<string>(
            "SELECT \"AccountDetail\" FROM \"AspNetUsers\" WHERE \"Id\" = @userId",
            new { userId }
        );
        if (accountDetail is null)
            return null;
        return JsonConvert.DeserializeObject<AccountSettings>(accountDetail, CamelCaseSerializerSettings.Instance);
    }

    public static async Task VerifyGithubAccount(this NpgsqlConnection connection, string userId, string gistUrl)
    {
        await connection.ExecuteAsync(
            "UPDATE \"AspNetUsers\" SET \"GithubGistUrl\" = @gistUrl WHERE \"Id\" = @userId",
            new { userId, gistUrl }
        );
    }

    public static async Task<bool> IsGithubAccountVerified(this NpgsqlConnection connection, string userId)
    {
        var githubGistUrl = await connection.QuerySingleOrDefaultAsync<string>(
            "SELECT \"GithubGistUrl\" FROM \"AspNetUsers\" WHERE \"Id\" = @userId",
            new { userId }
        );
        return !string.IsNullOrEmpty(githubGistUrl);
    }

    public static async Task<bool> UserOwnsPlugin(this NpgsqlConnection connection, string userId, PluginSlug pluginSlug)
    {
        return await connection.QuerySingleAsync<bool>(
            "SELECT EXISTS (SELECT * FROM users_plugins WHERE user_id=@userId AND plugin_slug=@pluginSlug);",
            new { pluginSlug = pluginSlug.ToString(), userId });
    }
    public static async Task<IEnumerable<string>> RetrievePluginUserIds(this NpgsqlConnection connection, PluginSlug pluginSlug)
    {
        return await connection.QueryAsync<string>(
            "SELECT user_id FROM users_plugins WHERE plugin_slug=@pluginSlug;",
            new { pluginSlug = pluginSlug.ToString() });
    }

    public static async Task<string?> RetrievePluginOwner(this NpgsqlConnection connection, PluginSlug pluginSlug)
    {
        return await connection.QueryFirstOrDefaultAsync<string>(
            "SELECT user_id FROM users_plugins WHERE plugin_slug=@pluginSlug AND is_primary_owner IS TRUE;",
            new { pluginSlug = pluginSlug.ToString() });
    }

    public static async Task AssignPluginPrimaryOwner(this NpgsqlConnection connection, PluginSlug pluginSlug, string userId)
    {
        using var tx = connection.BeginTransaction();
        await connection.ExecuteAsync(
            "UPDATE users_plugins SET is_primary_owner = FALSE WHERE plugin_slug = @pluginSlug AND is_primary_owner IS TRUE;",
            new { pluginSlug = pluginSlug.ToString() }, tx);

        await connection.ExecuteAsync(
            @"UPDATE users_plugins SET is_primary_owner = TRUE WHERE plugin_slug = @pluginSlug AND user_id = @userId;",
            new { pluginSlug = pluginSlug.ToString(), userId }, tx);
        await tx.CommitAsync();
    }

    public static async Task RevokePluginOwnership(this NpgsqlConnection connection, PluginSlug pluginSlug, string userId)
    {
        await connection.ExecuteAsync(
            @"UPDATE users_plugins SET is_primary_owner = FALSE WHERE plugin_slug = @pluginSlug AND user_id = @userId;",
            new { pluginSlug = pluginSlug.ToString(), userId });
    }

    public static async Task AddUserPlugin(this NpgsqlConnection connection, PluginSlug pluginSlug, string userId)
    {
        await connection.ExecuteAsync("INSERT INTO users_plugins VALUES (@userId, @pluginSlug) ON CONFLICT DO NOTHING",
            new { pluginSlug = pluginSlug.ToString(), userId });
    }

    public static async Task<bool> NewPlugin(this NpgsqlConnection connection, PluginSlug pluginSlug)
    {
        var count = await connection.ExecuteAsync("INSERT INTO plugins (slug) VALUES (@id) ON CONFLICT DO NOTHING;",
            new { id = pluginSlug.ToString() });
        return count == 1;
    }

    public static async Task UpdateBuild(this NpgsqlConnection connection, FullBuildId fullBuildId, BuildStates newState, JObject? buildInfo,
        PluginManifest? manifestInfo = null)
    {
        await connection.ExecuteAsync(
            "UPDATE builds " +
            "SET state=@state, " +
            "build_info=COALESCE(build_info || @build_info::JSONB, @build_info::JSONB, build_info), " +
            "manifest_info=COALESCE(@manifest_info::JSONB, manifest_info) " +
            "WHERE plugin_slug=@plugin_slug AND id=@buildId",
            new
            {
                state = newState.ToEventName(),
                build_info = buildInfo?.ToString(),
                manifest_info = manifestInfo?.ToString(),
                plugin_slug = fullBuildId.PluginSlug.ToString(),
                buildId = fullBuildId.BuildId
            });
    }

    public static async Task<PluginSlug[]> GetPluginsByUserId(this NpgsqlConnection connection, string userId)
    {
        return (await connection.QueryAsync<string>(
                "SELECT up.plugin_slug FROM users_plugins up " +
                "JOIN plugins p ON up.plugin_slug=p.slug " +
                "WHERE up.user_id=@userId;", new { userId }))
            .Select(s => PluginSlug.Parse(s)).ToArray();
    }

    public static async Task<bool> EnsureIdentifierOwnership(this NpgsqlConnection connection, PluginSlug pluginSlug, string identifier)
    {
        var pluginIdentifier =
            await connection.ExecuteScalarAsync<string?>("SELECT identifier FROM plugins WHERE slug=@pluginSlug", new { pluginSlug = pluginSlug.ToString() });
        if (pluginIdentifier is not null)
            return pluginIdentifier == identifier;
        try
        {
            return await connection.ExecuteAsync("UPDATE plugins SET identifier=@identifier WHERE slug=@pluginSlug AND identifier IS NULL",
                new { pluginSlug = pluginSlug.ToString(), identifier }) == 1;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return false;
        }
    }

    public static async Task<PluginSlug?> ResolvePluginSlug(this DBConnectionFactory connectionFactory, PluginSelector selector)
    {
        if (selector is PluginSelectorBySlug s)
            return s.PluginSlug;
        await using var conn = await connectionFactory.Open();
        return await conn.ResolvePluginSlug(selector);
    }

    public static async Task<PluginSlug?> ResolvePluginSlug(this NpgsqlConnection connection, PluginSelector selector)
    {
        if (selector is PluginSelectorBySlug s)
            return s.PluginSlug;
        if (selector is PluginSelectorByIdentifier i)
        {
            var slug = await connection.ExecuteScalarAsync<string?>("SELECT slug FROM plugins WHERE identifier=@identifier",
                new { identifier = i.Identifier });
            if (slug is null)
                return null;
            if (PluginSlug.TryParse(slug, out var o))
                return o;
        }

        return null;
    }

    public static Task InsertEvent(this NpgsqlConnection connection, string evtType, JObject data)
    {
        return connection.ExecuteAsync("INSERT INTO evts VALUES (@evtType, @evt::JSONB);", new { evtType, evt = data.ToString() });
    }

    public static async Task<bool> SetVersionBuild(this NpgsqlConnection connection, FullBuildId fullBuildId, PluginVersion version,
        PluginVersion? minBTCPayVersion, bool preRelease)
    {
        minBTCPayVersion ??= PluginVersion.Zero;

        return await connection.ExecuteAsync(
            "INSERT INTO versions AS v VALUES (@plugin_slug, @ver, @build_id, @btcpay_min_ver, @pre_release) " +
            "ON CONFLICT (plugin_slug, ver) DO UPDATE SET build_id = @build_id, btcpay_min_ver = @btcpay_min_ver, pre_release=@pre_release " +
            "WHERE v.pre_release IS TRUE AND (v.build_id != @build_id OR v.btcpay_min_ver != @btcpay_min_ver OR @pre_release IS FALSE);",
            new
            {
                plugin_slug = fullBuildId.PluginSlug.ToString(),
                ver = version.VersionParts,
                build_id = fullBuildId.BuildId,
                btcpay_min_ver = minBTCPayVersion.VersionParts,
                pre_release = preRelease
            }) == 1;
    }

    public static async Task<long> NewBuild(this NpgsqlConnection connection, PluginSlug pluginSlug, PluginBuildParameters buildParameters)
    {
        BuildInfo bi = new()
        {
            BuildConfig = buildParameters.BuildConfig,
            GitRepository = buildParameters.GitRepository,
            GitRef = buildParameters.GitRef,
            PluginDir = buildParameters.PluginDirectory
        };
        var buildId = await connection.ExecuteScalarAsync<long>("" +
                                                   "WITH cte AS " +
                                                   "( " +
                                                   " INSERT INTO builds_ids AS bi VALUES (@plugin_slug, 0)" +
                                                   "        ON CONFLICT (plugin_slug) DO UPDATE SET curr_id=bi.curr_id+1 " +
                                                   " RETURNING curr_id " +
                                                   ") " +
                                                   "INSERT INTO builds (plugin_slug, id, state, build_info) VALUES (@plugin_slug, (SELECT * FROM cte), @state, @buildInfo::JSONB) RETURNING id;",
            new
            {
                plugin_slug = pluginSlug.ToString(),
                state = BuildStates.Queued.ToEventName(),
                buildInfo = bi.ToString()
            });

        var currId = await connection.ExecuteScalarAsync<long>("SELECT curr_id FROM builds_ids WHERE plugin_slug = @plugin_slug",
            new { plugin_slug = pluginSlug.ToString() });

        if (currId == 0)
        {
            const string assignOwnerSql = """
                UPDATE users_plugins
                SET is_primary_owner = TRUE
                WHERE plugin_slug = @plugin_slug
                AND NOT EXISTS (
                    SELECT 1 FROM users_plugins WHERE plugin_slug = @plugin_slug AND is_primary_owner IS TRUE
                );
                """;
            await connection.ExecuteAsync(assignOwnerSql, new { plugin_slug = pluginSlug.ToString() });
        }
        return buildId;
    }

    // Methods related to getting / setting settings in the DB 
    public static Task<IEnumerable<(string key, string value)>> SettingsGetAllAsync(this NpgsqlConnection connection)
    {
        var query = "SELECT key, value FROM settings";
        return connection.QueryAsync<(string key, string value)>(query);
    }


    public static Task<string> SettingsGetAsync(this NpgsqlConnection connection, string key)
    {
        var query = "SELECT value FROM settings WHERE key = @key";
        return connection.QuerySingleOrDefaultAsync<string>(query, new { key });
    }

    public static Task<int> SettingsSetAsync(this NpgsqlConnection connection, string key, string value)
    {
        var query = """
                    INSERT INTO settings(key, value) 
                    VALUES(@key, @value)
                    ON CONFLICT (key) DO UPDATE 
                    SET value = EXCLUDED.value
                    """;
        return connection.ExecuteAsync(query, new { key, value });
    }

    public static Task<int> SettingsDeleteAsync(this NpgsqlConnection connection, string key)
    {
        var query = """
                    DELETE FROM settings
                    WHERE key = @key
                    """;
        return connection.ExecuteAsync(query, new { key });
    }

    public static async Task<bool> GetVerifiedEmailForPluginPublishSetting(this NpgsqlConnection connection)
    {
        var settingValue = await connection.QuerySingleOrDefaultAsync<string>("SELECT value FROM settings WHERE key = 'VerifiedEmailForPluginPublish'");
        if (settingValue == null)
        {
            await connection.ExecuteAsync("INSERT INTO settings (key, value) VALUES ('VerifiedEmailForPluginPublish', 'true')");
            settingValue = "true";
        }

        return bool.TryParse(settingValue, out var result) && result;
    }

    public static async Task UpdateVerifiedEmailForPluginPublishSetting(this NpgsqlConnection connection, bool newValue)
    {
        var stringValue = newValue.ToString().ToLower();
        await connection.ExecuteAsync("UPDATE settings SET value = @Value WHERE key = 'VerifiedEmailForPluginPublish'",
            new { Value = stringValue });
    }
}
