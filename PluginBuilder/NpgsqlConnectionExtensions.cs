#nullable enable
using Dapper;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Npgsql;
using PluginBuilder.DataModels;
using PluginBuilder.Services;

namespace PluginBuilder
{
    public static class NpgsqlConnectionExtensions
    {
        public static async Task<PluginSettings?> GetSettings(this NpgsqlConnection connection, PluginSlug pluginSlug)
        {
            var r = await connection.QueryFirstOrDefaultAsync<string>("SELECT settings FROM plugins WHERE slug=@pluginSlug",
                new
                {
                    pluginSlug = pluginSlug.ToString()
                });
            if (r is null)
                return null;
            return JsonConvert.DeserializeObject<PluginSettings>(r, CamelCaseSerializerSettings.Instance);
        }
        public static async Task<bool> SetSettings(this NpgsqlConnection connection, PluginSlug pluginSlug, PluginSettings pluginSettings)
        {
            var count = await connection.ExecuteAsync("UPDATE plugins SET settings=@settings::JSONB WHERE slug=@pluginSlug",
                new
                {
                    pluginSlug = pluginSlug.ToString(),
                    settings = JsonConvert.SerializeObject(pluginSettings, CamelCaseSerializerSettings.Instance)
                });
            return count == 1;
        }
        public static async Task SetAccountDetailSettings(this NpgsqlConnection connection, AccountSettings accountSettings, string userId)
        {
            await connection.ExecuteAsync(
                "UPDATE \"AspNetUsers\" SET \"AccountDetail\" = @settings::JSONB WHERE \"Id\" = @userId",
                new
                {
                    userId,
                    settings = JsonConvert.SerializeObject(accountSettings, CamelCaseSerializerSettings.Instance)
                }
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

        public static async Task<bool> UserOwnsPlugin(this NpgsqlConnection connection, string userId, PluginSlug pluginSlug)
        {
            return await connection.QuerySingleAsync<bool>(
                "SELECT EXISTS (SELECT * FROM users_plugins WHERE user_id=@userId AND plugin_slug=@pluginSlug);",
                new
                {
                    pluginSlug = pluginSlug.ToString(),
                    userId = userId
                });
        }
        public static async Task AddUserPlugin(this NpgsqlConnection connection, PluginSlug pluginSlug, string userId)
        {
            await connection.ExecuteAsync("INSERT INTO users_plugins VALUES (@userId, @pluginSlug) ON CONFLICT DO NOTHING",
                new
                {
                    pluginSlug = pluginSlug.ToString(),
                    userId = userId
                });
        }
        public static async Task<bool> NewPlugin(this NpgsqlConnection connection, PluginSlug pluginSlug)
        {
            var count = await connection.ExecuteAsync("INSERT INTO plugins (slug) VALUES (@id) ON CONFLICT DO NOTHING;",
                new
                {
                    id = pluginSlug.ToString(),
                });
            return count == 1;
        }
        public static async Task UpdateBuild(this NpgsqlConnection connection, FullBuildId fullBuildId, BuildStates newState, JObject? buildInfo, PluginManifest? manifestInfo = null)
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
                "WHERE up.user_id=@userId;", new { userId = userId }))
                .Select(s => PluginSlug.Parse(s)).ToArray();
        }

        public static async Task<bool> EnsureIdentifierOwnership(this NpgsqlConnection connection, PluginSlug pluginSlug, string identifier)
        {
            var pluginIdentifier = await connection.ExecuteScalarAsync<string?>("SELECT identifier FROM plugins WHERE slug=@pluginSlug", new { pluginSlug = pluginSlug.ToString() });
            if (pluginIdentifier is not null)
                return pluginIdentifier == identifier;
            try
            {
                return await connection.ExecuteAsync("UPDATE plugins SET identifier=@identifier WHERE slug=@pluginSlug AND identifier IS NULL", new { pluginSlug = pluginSlug.ToString(), identifier = identifier }) == 1;
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
            else if (selector is PluginSelectorByIdentifier i)
            {
                var slug = await connection.ExecuteScalarAsync<string?>("SELECT slug FROM plugins WHERE identifier=@identifier", new { identifier  = i.Identifier });
                if (slug is null)
                    return null;
                if (PluginSlug.TryParse(slug, out var o))
                    return o;
            }
            return null;
        }

        public static Task InsertEvent(this NpgsqlConnection connection, string evtType, JObject data)
        {
            return connection.ExecuteAsync("INSERT INTO evts VALUES (@evtType, @evt::JSONB);", new
            {
                evtType = evtType,
                evt = data.ToString()
            });
        }

        public static async Task<bool> SetVersionBuild(this NpgsqlConnection connection, FullBuildId fullBuildId, PluginVersion version, PluginVersion? minBTCPayVersion, bool preRelease)
        {
            minBTCPayVersion ??= PluginVersion.Zero;

            return (await connection.ExecuteAsync(
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
                    })) == 1;
        }
        public static Task<long> NewBuild(this NpgsqlConnection connection, PluginSlug pluginSlug, PluginBuildParameters buildParameters)
        {
            var bi = new BuildInfo()
            {
                BuildConfig = buildParameters.BuildConfig,
                GitRepository = buildParameters.GitRepository,
                GitRef = buildParameters.GitRef,
                PluginDir = buildParameters.PluginDirectory
            };
            return connection.ExecuteScalarAsync<long>("" +
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
        }
        
        // Add the method to get all the settings from database 
        public static Task<IEnumerable<(string key, string value)>> GetAllSettingsAsync(this NpgsqlConnection connection)
        {
            // SQL query to fetch all the settings from settings table
            var query = "SELECT key, value FROM settings";
            return connection.QueryAsync<(string key, string value)>(query);
        }
        
            
        public static Task<string> GetSettingAsync(this NpgsqlConnection connection, string key)
        {
            // SQL query to fetch the value from settings table
            var query = "SELECT value FROM settings WHERE key = @key";
            return connection.QuerySingleOrDefaultAsync<string>(query, new { key });
        }

        public static Task<int> SetSettingAsync(this NpgsqlConnection connection, string key, string value)
        {
            var query = $"""
                INSERT INTO settings(key, value) 
                VALUES(@key, @value)
                ON CONFLICT (key) DO UPDATE 
                SET value = EXCLUDED.value
                """;
            return connection.ExecuteAsync(query, new { key, value });
        }
    }
}
