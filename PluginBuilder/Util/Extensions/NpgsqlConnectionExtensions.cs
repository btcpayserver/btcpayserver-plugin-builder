using Dapper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Npgsql;
using PluginBuilder.DataModels;
using PluginBuilder.JsonConverters;
using PluginBuilder.Services;
using PluginBuilder.ViewModels;
using PluginBuilder.ViewModels.Admin;
using PluginBuilder.ViewModels.Plugin;

namespace PluginBuilder.Util.Extensions;

public static class NpgsqlConnectionExtensions
{

    #region Methods relating to plugin settings
    public static async Task<PluginSettings?> GetSettings(this NpgsqlConnection connection, PluginSlug pluginSlug)
    {
        var r = await connection.QueryFirstOrDefaultAsync<string>("SELECT settings FROM plugins WHERE slug=@pluginSlug",
            new { pluginSlug = pluginSlug.ToString() });
        if (r is null)
            return null;
        return JsonConvert.DeserializeObject<PluginSettings>(r, CamelCaseSerializerSettings.Instance);
    }

    public static async Task<bool> SetPluginSettings(this NpgsqlConnection connection, PluginSlug pluginSlug, PluginSettings? pluginSettings, PluginVisibilityEnum? visibility = null)
    {
        var settingsJson = "{}";
        if (pluginSettings != null)
            settingsJson = JsonConvert.SerializeObject(pluginSettings, CamelCaseSerializerSettings.Instance);

        var sql = "UPDATE plugins SET settings = @settings::JSONB WHERE slug = @pluginSlug";
        if (visibility != null)
            sql = "UPDATE plugins SET settings = @settings::JSONB, visibility = @visibility::plugin_visibility_enum WHERE slug = @pluginSlug";

        var affectedRows = await connection.ExecuteAsync(sql, new { pluginSlug = pluginSlug.ToString(), settings = settingsJson, visibility = visibility?.ToString().ToLowerInvariant() });
        return affectedRows == 1;
    }

    public static async Task<PluginViewModel?> GetPluginDetails(this NpgsqlConnection connection, PluginSlug pluginSlug)
    {
        return await connection.QueryFirstOrDefaultAsync<PluginViewModel>(
            "SELECT slug AS \"PluginSlug\", identifier, settings::text AS settings, visibility::text AS visibility FROM plugins WHERE slug=@pluginSlug",
            new { pluginSlug = pluginSlug.ToString() });
    }

    #endregion


    #region Methods relating to User account settings

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

    public static async Task<string?> GetUserIdByNpubAsync(this NpgsqlConnection conn, string npub)
    {
        const string sql = """
                           SELECT "Id"
                           FROM "AspNetUsers"
                           WHERE lower(trim("AccountDetail"->'nostr'->>'npub')) = lower(trim(@npub))
                           LIMIT 1;
                           """;
        return await conn.ExecuteScalarAsync<string?>(sql, new { npub });
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

    public static async Task<bool> IsSocialAccountsVerified(this NpgsqlConnection connection, string userId)
    {
        var result = await connection.QuerySingleOrDefaultAsync<(string GithubGistUrl, string AccountDetail)>(
            "SELECT \"GithubGistUrl\", \"AccountDetail\" FROM \"AspNetUsers\" WHERE \"Id\" = @userId AND \"EmailConfirmed\" = true",
            new { userId });

        if (string.IsNullOrEmpty(result.AccountDetail) || string.IsNullOrEmpty(result.GithubGistUrl))
            return false;

        var accountSettings = SafeJson.Deserialize<AccountSettings>(result.AccountDetail);
        return !string.IsNullOrWhiteSpace(accountSettings?.Nostr?.Npub);
    }

    #endregion


    #region Methods relating to plugin owners/users

    public static async Task<bool> UserOwnsPlugin(this NpgsqlConnection connection, string userId, PluginSlug pluginSlug)
    {
        return await connection.QuerySingleAsync<bool>("SELECT EXISTS (SELECT * FROM users_plugins WHERE user_id=@userId AND plugin_slug=@pluginSlug);",
            new { pluginSlug = pluginSlug.ToString(), userId });
    }
    public static async Task<IEnumerable<string>> RetrievePluginUserIds(this NpgsqlConnection connection, PluginSlug pluginSlug)
    {
        return await connection.QueryAsync<string>("SELECT user_id FROM users_plugins WHERE plugin_slug=@pluginSlug;",
            new { pluginSlug = pluginSlug.ToString() });
    }

    public static async Task<string?> RetrievePluginPrimaryOwner(this NpgsqlConnection connection, PluginSlug pluginSlug)
    {
        return await connection.QueryFirstOrDefaultAsync<string>("SELECT user_id FROM users_plugins WHERE plugin_slug=@pluginSlug AND is_primary_owner IS TRUE;",
            new { pluginSlug = pluginSlug.ToString() });
    }

    public static async Task<bool> AssignPluginPrimaryOwner(this NpgsqlConnection connection, PluginSlug pluginSlug, string userId)
    {
        await using var tx = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync("UPDATE users_plugins SET is_primary_owner = FALSE WHERE plugin_slug = @pluginSlug AND is_primary_owner IS TRUE;",
            new { pluginSlug = pluginSlug.ToString() }, tx);

        var updated = await connection.ExecuteAsync(@"UPDATE users_plugins SET is_primary_owner = TRUE WHERE plugin_slug = @pluginSlug AND user_id = @userId;",
            new { pluginSlug = pluginSlug.ToString(), userId }, tx);

        if (updated != 1)
            return false;

        await tx.CommitAsync();
        return true;
    }

    public static async Task<bool> RevokePluginPrimaryOwnership(this NpgsqlConnection connection, PluginSlug pluginSlug, string userId)
    {
        var updated = await connection.ExecuteAsync(@"UPDATE users_plugins SET is_primary_owner = FALSE WHERE plugin_slug = @pluginSlug AND user_id = @userId;",
            new { pluginSlug = pluginSlug.ToString(), userId });
        return updated == 1;
    }

    public static async Task AddUserPlugin(this NpgsqlConnection connection, PluginSlug pluginSlug, string userId, bool isPrimary = false)
    {
        await connection.ExecuteAsync("INSERT INTO users_plugins (user_id, plugin_slug, is_primary_owner) VALUES (@userId, @pluginSlug, @isPrimary) ON CONFLICT DO NOTHING",
            new { pluginSlug = pluginSlug.ToString(), userId, isPrimary });
    }

    public static Task<int> RemovePluginOwner(this NpgsqlConnection connection, PluginSlug pluginSlug, string userId)
    {
        return connection.ExecuteAsync("DELETE FROM users_plugins WHERE plugin_slug = @pluginSlug AND user_id = @userId;",
            new { pluginSlug = pluginSlug.ToString(), userId });
    }

    public static async Task<List<OwnerVm>> GetPluginOwners(this NpgsqlConnection connection, PluginSlug pluginSlug)
    {
        const string sql = """
                           SELECT
                               u."Id"               AS "UserId",
                               up.is_primary_owner  AS "IsPrimary",
                               u."Email",
                               u."AccountDetail",
                               u."EmailConfirmed"
                           FROM users_plugins up
                           JOIN "AspNetUsers" u ON u."Id" = up.user_id
                           WHERE up.plugin_slug = @slug
                           ORDER BY up.is_primary_owner DESC, COALESCE(u."Email", u."Id");
                           """;

        var owners = await connection.QueryAsync<OwnerVm>(sql, new { slug = pluginSlug.ToString() });
        return owners.ToList();
    }

    #endregion


    #region Methods relating to plugin and builds

    public static async Task<bool> NewPlugin(this NpgsqlConnection connection, PluginSlug pluginSlug, string userId)
    {
        var count = await connection.ExecuteAsync("INSERT INTO plugins (slug) VALUES (@id) ON CONFLICT DO NOTHING;",
            new { id = pluginSlug.ToString() });

        if (count != 1) return false;

        await connection.AddUserPlugin(pluginSlug, userId, true);
        return true;
    }

    public static async Task UpdateBuild(this NpgsqlConnection connection, FullBuildId fullBuildId, BuildStates newState, JObject? buildInfo,
        PluginManifest? manifestInfo = null)
    {
        await connection.ExecuteAsync(
            "UPDATE builds " +
            "SET state = @state, " +
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

    public static async Task<bool> UpdateVersionReleaseStatus(this NpgsqlConnection connection, PluginSlug pluginSlug, string command, PluginVersion version, SignatureProof? signatureProof = null)
    {
        var updated = await connection.ExecuteAsync(
            "UPDATE versions SET pre_release = @preRelease, signatureproof = CASE WHEN @hasSignature THEN @signatureproof::JSONB WHEN @preRelease THEN NULL ELSE signatureproof END " +
            "WHERE plugin_slug = @pluginSlug AND ver = @version",
            new
            {
                signatureproof = signatureProof != null ? JsonConvert.SerializeObject(signatureProof, CamelCaseSerializerSettings.Instance) : null,
                hasSignature = signatureProof != null,
                pluginSlug = pluginSlug.ToString(),
                version = version.VersionParts,
                preRelease = command == "unrelease"
            });
        return updated == 1;
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
            return await connection.ExecuteAsync("UPDATE plugins SET identifier = @identifier WHERE slug=@pluginSlug AND identifier IS NULL",
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
            "ON CONFLICT (plugin_slug, ver) DO UPDATE SET build_id = @build_id, btcpay_min_ver = @btcpay_min_ver, pre_release = @pre_release " +
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

    public static async Task<long> NewBuild(this NpgsqlConnection connection, PluginSlug pluginSlug, PluginBuildParameters buildParameters,
        FirstBuildEvent? firstBuildEvent = null)
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
        return buildId;
    }

    public static async Task<long> GetLatestPluginBuildNumber(this NpgsqlConnection connection, PluginSlug pluginSlug)
    {
        return await connection.ExecuteScalarAsync<long>("SELECT curr_id FROM builds_ids WHERE plugin_slug = @plugin_slug", new { plugin_slug = pluginSlug.ToString() });
    }

    #endregion


    #region Methods related to getting / setting settings in the DB

    public static Task<IEnumerable<(string key, string value)>> SettingsGetAllAsync(this NpgsqlConnection connection)
    {
        var query = "SELECT key, value FROM settings";
        return connection.QueryAsync<(string key, string value)>(query);
    }

    public static Task<string?> SettingsGetAsync(this NpgsqlConnection connection, string key)
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

    public static async Task SettingsInitialize(this NpgsqlConnection connection)
    {
        var query = "SELECT key, value FROM settings";
        var result = (await connection.QueryAsync<(string key, string value)>(query)).ToList();
        if (result.All(r => r.key != SettingsKeys.FirstPluginBuildReviewers))
        {
            await connection.ExecuteAsync("INSERT INTO settings (key, value) VALUES (@key, @value)",
                new { key = SettingsKeys.FirstPluginBuildReviewers, value = "" });
        }

        if (result.All(r => r.key != SettingsKeys.VerifiedEmailForPluginPublish))
        {
            await connection.ExecuteAsync("INSERT INTO settings (key, value) VALUES (@key, @value)",
                new { key = SettingsKeys.VerifiedEmailForPluginPublish, value = "true" });
        }
        if (result.All(r => r.key != SettingsKeys.VerifiedEmailForLogin))
        {
            await connection.ExecuteAsync("INSERT INTO settings (key, value) VALUES (@key, @value)",
                new { key = SettingsKeys.VerifiedEmailForLogin, value = "true" });
        }

        if (result.All(r => r.key != SettingsKeys.VerifiedGithub))
        {
            await connection.ExecuteAsync("INSERT INTO settings (key, value) VALUES (@key, @value)",
                new { key = SettingsKeys.VerifiedGithub, value = "false" });
        }

        if (result.All(r => r.key != SettingsKeys.VerifiedNostr))
        {
            await connection.ExecuteAsync(
                "INSERT INTO settings (key, value) VALUES (@key, @value)",
                new { key = SettingsKeys.VerifiedNostr, value = "false" });
        }

        if (result.All(r => r.key != SettingsKeys.NostrRelays))
        {

            var json = JsonConvert.SerializeObject(NostrService.DefaultRelays);
            await connection.ExecuteAsync(
                "INSERT INTO settings (key, value) VALUES (@key, @value)",
                new { key = SettingsKeys.NostrRelays, value = json });
        }
    }

    public static async Task<bool> GetVerifiedEmailForPluginPublishSetting(this NpgsqlConnection connection)
    {
        var settingValue = await SettingsGetAsync(connection, SettingsKeys.VerifiedEmailForPluginPublish);
        return bool.TryParse(settingValue, out var result) && result;
    }

    public static async Task UpdateVerifiedEmailForPluginPublishSetting(this NpgsqlConnection connection, bool newValue)
    {
        var stringValue = newValue.ToString().ToLowerInvariant();
        await connection.ExecuteAsync("UPDATE settings SET value = @Value WHERE key = @Key",
            new { Value = stringValue, Key = SettingsKeys.VerifiedEmailForPluginPublish });
    }

    public static async Task<bool> GetVerifiedEmailForLoginSetting(this NpgsqlConnection connection)
    {
        var settingValue = await SettingsGetAsync(connection, SettingsKeys.VerifiedEmailForLogin);
        return bool.TryParse(settingValue, out var result) && result;
    }

    public static Task<string?> GetFirstPluginBuildReviewersSetting(this NpgsqlConnection connection)
    {
        return SettingsGetAsync(connection, SettingsKeys.FirstPluginBuildReviewers);
    }

    public static async Task<bool> GetVerifiedGithubSetting(this NpgsqlConnection connection)
    {
        var v = await SettingsGetAsync(connection, SettingsKeys.VerifiedGithub);
        return bool.TryParse(v, out var b) && b;
    }

    public static async Task<bool> GetVerifiedNostrSetting(this NpgsqlConnection connection)
    {
        var v = await SettingsGetAsync(connection, SettingsKeys.VerifiedNostr);
        return bool.TryParse(v, out var b) && b;
    }

    public static async Task<string[]> GetNostrRelaysSetting(this NpgsqlConnection connection)
    {
        var raw = await connection.QueryFirstOrDefaultAsync<string>(
            "SELECT value FROM settings WHERE key=@k LIMIT 1",
            new { k = SettingsKeys.NostrRelays });

        var relays = JsonConvert.DeserializeObject<string[]?>(raw ?? string.Empty);
        return relays is { Length: > 0 } ? relays : NostrService.DefaultRelays.ToArray();
    }

    #endregion


    #region Plugin Reviewers and reviews

    public static async Task<IEnumerable<PluginReviewViewModel>> GetPluginReviews(this NpgsqlConnection connection, string pluginSlug)
    {
        return await connection.QueryAsync<PluginReviewViewModel>(
            """
            SELECT plugin_slug AS PluginSlug, user_id AS UserId, rating AS Rating, body AS Body, plugin_version AS PluginVersion, helpful_voters AS HelpfulVoters, author_username AS AuthorName,
            author_profile_url AS AuthorProfileUrl, author_avatar_url AS AuthorAvatarUrl, created_at AS CreatedAt, updated_at AS UpdatedAt FROM plugin_reviews WHERE plugin_slug = @pluginSlug
        """, new { pluginSlug });
    }

    public static Task UpsertPluginReview(this NpgsqlConnection connection, PluginReviewViewModel model)
    {
        const string sql = """
                               INSERT INTO plugin_reviews
                                   (plugin_slug, user_id, rating, body, plugin_version, reviewer_id, created_at, updated_at)
                               VALUES
                                   (@plugin_slug, @user_id, @rating, NULLIF(@body,''), @plugin_version, @reviewer_id, NOW(), NOW())
                               ON CONFLICT (plugin_slug, reviewer_id)
                               DO UPDATE SET
                                   rating         = EXCLUDED.rating,
                                   body           = EXCLUDED.body,
                                   plugin_version = EXCLUDED.plugin_version,
                                   updated_at     = NOW(),
                                   helpful_voters = CASE
                                   WHEN (plugin_reviews.rating IS DISTINCT FROM EXCLUDED.rating)
                                     OR (COALESCE(plugin_reviews.body,'') IS DISTINCT FROM COALESCE(EXCLUDED.body,''))
                                   THEN '{}'::jsonb
                                   ELSE plugin_reviews.helpful_voters
                                   END;
                           """;

        return connection.ExecuteAsync(sql, new
        {
            plugin_slug = model.PluginSlug,
            user_id = model.UserId,
            rating = model.Rating,
            body = model.Body,
            reviewer_id = model.ReviewerId,
            plugin_version = model.PluginVersion
        });
    }

    public static async Task<bool> DeleteReviewAsync(
        this NpgsqlConnection conn,
        PluginSlug pluginSlug,
        long reviewId,
        string userId,
        bool isAdmin)
    {
        const string sql = """
                           DELETE FROM plugin_reviews
                           WHERE id = @id
                             AND plugin_slug = @slug
                             AND ( @isAdmin OR user_id = @userId )
                           """;

        var rows = await conn.ExecuteAsync(sql, new
        {
            id = reviewId,
            slug = pluginSlug.ToString(),
            userId,
            isAdmin
        });

        return rows > 0;
    }

    public static Task<bool?> GetReviewHelpfulVoteAsync(
            this NpgsqlConnection conn,
            PluginSlug pluginSlug,
            long reviewId,
            string userId)
    {
        const string sql = """
                               SELECT (helpful_voters ->> @userId)::boolean
                               FROM plugin_reviews
                               WHERE id = @id AND plugin_slug = @slug
                               """;
        return conn.ExecuteScalarAsync<bool?>(sql, new
        {
            id = reviewId,
            slug = pluginSlug.ToString(),
            userId
        });
    }

    public static async Task<bool> RemoveReviewHelpfulVoteAsync(
        this NpgsqlConnection conn,
        PluginSlug pluginSlug,
        long reviewId,
        string userId)
    {
        const string sql = """
                               UPDATE plugin_reviews
                               SET helpful_voters = helpful_voters - @userId
                               WHERE id = @id
                                 AND plugin_slug = @slug
                                 AND user_id <> @userId;
                               """;
        var rows = await conn.ExecuteAsync(sql, new
        {
            id = reviewId,
            slug = pluginSlug.ToString(),
            userId
        });
        return rows > 0;
    }

    public static async Task<bool> UpsertReviewHelpfulVoteAsync(
        this NpgsqlConnection conn,
        PluginSlug pluginSlug,
        long reviewId,
        string userId,
        bool isHelpful)
    {
        const string sql = """
                               UPDATE plugin_reviews
                               SET helpful_voters = jsonb_set((helpful_voters),
                                   ARRAY[@userId],
                                   to_jsonb(@isHelpful),
                                   true)
                               WHERE id = @id
                                 AND plugin_slug = @slug
                                 AND user_id <> @userId;
                               """;
        var rows = await conn.ExecuteAsync(sql, new
        {
            id = reviewId,
            slug = pluginSlug.ToString(),
            userId,
            isHelpful
        });
        return rows > 0;
    }

    public static async Task<long> CreatePluginReviewer(this NpgsqlConnection connection, ImportReviewViewModel reviewModel)
    {
        const string sql = """
            INSERT INTO plugin_reviewers (user_id, username, source, profile_url, avatar_url, created_at, updated_at)
            VALUES (@user_id, @username, @source, @profile_url, @avatar_url, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
            RETURNING id
            """;

        return await connection.ExecuteScalarAsync<long>(sql, new
        {
            user_id = reviewModel.SelectedUserId,
            username = reviewModel.ReviewerName,
            source = reviewModel.LinkExistingUser ? "system" : reviewModel.Source.ToString().ToLower(),
            profile_url = reviewModel.ReviewerProfileUrl,
            avatar_url = reviewModel.ReviewerAvatarUrl
        });
    }

    public static async Task<long> CreateOrUpdatePluginReviewer(this NpgsqlConnection connection, ImportReviewViewModel reviewModel)
    {
        const string sql = """
            WITH existing AS (
                SELECT id FROM plugin_reviewers
                WHERE (user_id = @user_id AND @user_id IS NOT NULL)
                   OR (source = @source AND username = @username AND @username IS NOT NULL)
                LIMIT 1
            ),
            inserted AS (
                INSERT INTO plugin_reviewers (user_id, username, source, profile_url, avatar_url, created_at, updated_at)
                SELECT @user_id, @username, @source, @profile_url, @avatar_url, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
                WHERE NOT EXISTS (SELECT 1 FROM existing)
                RETURNING id
            ),
            updated AS (
                UPDATE plugin_reviewers p
                SET username = @username, source = @source, profile_url = @profile_url, avatar_url = @avatar_url, updated_at = CURRENT_TIMESTAMP
                FROM existing WHERE p.id = existing.id
                RETURNING p.id
            )
            SELECT id FROM inserted
            UNION ALL
            SELECT id FROM updated
            LIMIT 1;
            """;

        return await connection.ExecuteScalarAsync<long>(sql, new
        {
            user_id = reviewModel.SelectedUserId,
            username = reviewModel.ReviewerName,
            source = reviewModel.LinkExistingUser ? "system" : reviewModel.Source.ToString().ToLower(),
            profile_url = reviewModel.ReviewerProfileUrl,
            avatar_url = reviewModel.ReviewerAvatarUrl
        });
    }

    public static async Task<PluginReviewerViewModel?> GetPluginReviewer(this NpgsqlConnection connection, string? userId, string? username)
    {
        const string sql = """
            SELECT id, user_id, username, source, profile_url, avatar_url
            FROM   plugin_reviewers
            WHERE  (user_id = @userId OR @userId IS NULL) AND (username = @username OR @username IS NULL)
            ORDER  BY user_id NULLS LAST
            LIMIT  1
            """;
        return await connection.QueryFirstOrDefaultAsync<PluginReviewerViewModel?>(sql, new { userId, username });
    }

    #endregion


    #region Methods relating to plugin listing requests

    public static async Task<int> CreateListingRequest(this NpgsqlConnection connection, PluginSlug pluginSlug, string releaseNote, string telegramMessage, string userReviews, DateTimeOffset? announcementDate)
    {
        const string sql = """
            INSERT INTO plugin_listing_requests (plugin_slug, release_note, telegram_verification_message, user_reviews, announcement_date, status, submitted_at)
            VALUES (@pluginSlug, @releaseNote, @telegramMessage, @userReviews, @announcementDate, 'pending', CURRENT_TIMESTAMP)
            RETURNING id
            """;
        
        return await connection.ExecuteScalarAsync<int>(sql, new
        {
            pluginSlug = pluginSlug.ToString(),
            releaseNote,
            telegramMessage,
            userReviews,
            announcementDate
        });
    }

    public static async Task<PluginListingRequest?> GetListingRequest(this NpgsqlConnection connection, int requestId)
    {
        const string sql = """
            SELECT id AS "Id", 
                   plugin_slug AS "PluginSlug", 
                   release_note AS "ReleaseNote", 
                   telegram_verification_message AS "TelegramVerificationMessage", 
                   user_reviews AS "UserReviews", 
                   announcement_date AS "AnnouncementDate", 
                   status AS "Status", 
                   submitted_at AS "SubmittedAt", 
                   reviewed_at AS "ReviewedAt", 
                   reviewed_by AS "ReviewedBy", 
                   rejection_reason AS "RejectionReason"
            FROM plugin_listing_requests
            WHERE id = @requestId
            """;
        
        return await connection.QueryFirstOrDefaultAsync<PluginListingRequest>(sql, new { requestId });
    }

    public static async Task<PluginListingRequest?> GetPendingListingRequestForPlugin(this NpgsqlConnection connection, PluginSlug pluginSlug)
    {
        const string sql = """
            SELECT id AS "Id", 
                   plugin_slug AS "PluginSlug", 
                   release_note AS "ReleaseNote", 
                   telegram_verification_message AS "TelegramVerificationMessage", 
                   user_reviews AS "UserReviews", 
                   announcement_date AS "AnnouncementDate", 
                   status AS "Status", 
                   submitted_at AS "SubmittedAt", 
                   reviewed_at AS "ReviewedAt", 
                   reviewed_by AS "ReviewedBy", 
                   rejection_reason AS "RejectionReason"
            FROM plugin_listing_requests
            WHERE plugin_slug = @pluginSlug AND status = 'pending'
            ORDER BY submitted_at DESC
            LIMIT 1
            """;
        
        return await connection.QueryFirstOrDefaultAsync<PluginListingRequest>(sql, new { pluginSlug = pluginSlug.ToString() });
    }

    public static async Task<bool> ApproveListingRequest(this NpgsqlConnection connection, int requestId, string reviewedBy)
    {
        const string sql = """
            UPDATE plugin_listing_requests SET status = 'approved', reviewed_at = CURRENT_TIMESTAMP, reviewed_by = @reviewedBy
            WHERE id = @requestId AND status = 'pending'
            """;
        
        var affected = await connection.ExecuteAsync(sql, new { requestId, reviewedBy });
        return affected == 1;
    }

    public static async Task<bool> RejectListingRequest(this NpgsqlConnection connection, int requestId, string reviewedBy, string rejectionReason)
    {
        const string sql = """
            UPDATE plugin_listing_requests
            SET status = 'rejected', reviewed_at = CURRENT_TIMESTAMP, reviewed_by = @reviewedBy, rejection_reason = @rejectionReason
            WHERE id = @requestId AND status = 'pending'
            """;
        
        var affected = await connection.ExecuteAsync(sql, new { requestId, reviewedBy, rejectionReason });
        return affected == 1;
    }

    public static async Task<bool> HasPendingListingRequest(this NpgsqlConnection connection, PluginSlug pluginSlug)
    {
        const string sql = """
            SELECT EXISTS(SELECT 1 FROM plugin_listing_requests WHERE plugin_slug = @pluginSlug AND status = 'pending')
            """;
        
        return await connection.ExecuteScalarAsync<bool>(sql, new { pluginSlug = pluginSlug.ToString() });
    }

    public static async Task<PluginListingRequest?> GetLatestRejectedListingRequestForPlugin(this NpgsqlConnection connection, PluginSlug pluginSlug)
    {
        const string sql = """
            SELECT id AS "Id", 
                   plugin_slug AS "PluginSlug", 
                   release_note AS "ReleaseNote", 
                   telegram_verification_message AS "TelegramVerificationMessage", 
                   user_reviews AS "UserReviews", 
                   announcement_date AS "AnnouncementDate", 
                   status AS "Status", 
                   submitted_at AS "SubmittedAt", 
                   reviewed_at AS "ReviewedAt", 
                   reviewed_by AS "ReviewedBy", 
                   rejection_reason AS "RejectionReason"
            FROM plugin_listing_requests
            WHERE plugin_slug = @pluginSlug AND status = 'rejected'
            ORDER BY submitted_at DESC
            LIMIT 1
            """;
        
        return await connection.QueryFirstOrDefaultAsync<PluginListingRequest>(sql, new { pluginSlug = pluginSlug.ToString() });
    }

    public static async Task<int> GetPendingListingRequestsCount(this NpgsqlConnection connection)
    {
        const string sql = """
            SELECT COUNT(*) FROM plugin_listing_requests WHERE status = 'pending'
            """;
        
        return await connection.ExecuteScalarAsync<int>(sql);
    }

    #endregion
}
