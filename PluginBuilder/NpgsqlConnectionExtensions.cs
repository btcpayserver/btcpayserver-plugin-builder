using Dapper;
using Newtonsoft.Json.Linq;
using Npgsql;

namespace PluginBuilder
{
    public static class NpgsqlConnectionExtensions
    {
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
        public static async Task UpdateBuild(this NpgsqlConnection connection, FullBuildId fullBuildId, string newState, JObject? buildInfo, PluginManifest? manifestInfo = null)
        {
            await connection.ExecuteAsync(
                "UPDATE builds " +
                "SET state=@state, " +
                "build_info=COALESCE(build_info || @build_info::JSONB, @build_info::JSONB, build_info), " +
                "manifest_info=COALESCE(@manifest_info::JSONB, manifest_info) " +
                "WHERE plugin_slug=@plugin_slug AND id=@buildId", 
                new
                {
                    state = newState,
                    build_info = buildInfo?.ToString(),
                    manifest_info = manifestInfo?.ToString(),
                    plugin_slug = fullBuildId.PluginSlug.ToString(),
                    buildId = fullBuildId.BuildId
                });
        }
        public static async Task<PluginSlug[]> GetPluginsByUserId(this NpgsqlConnection connection, string userId)
        {
            return (await connection.QueryAsync<string>(
                "SELECT p.slug FROM plugins p JOIN users_plugins up ON up.plugin_slug=p.slug;"))
                .Select(s => PluginSlug.Parse(s)).ToArray();
        }
        public static Task SetVersionBuild(this NpgsqlConnection connection, FullBuildId fullBuildId, int[] version, int[]? minBTCPayVersion)
        {
            minBTCPayVersion ??= new int[] { 0,0,0,0 };
            return connection.ExecuteAsync(
                "INSERT INTO versions VALUES (@plugin_slug, @ver, @build_id, @btcpay_min_ver) " +
                "ON CONFLICT (plugin_slug, ver) DO UPDATE SET build_id = @build_id, btcpay_min_ver = @btcpay_min_ver;",
                new
                {
                    plugin_slug = fullBuildId.PluginSlug.ToString(),
                    ver = version,
                    build_id = fullBuildId.BuildId,
                    btcpay_min_ver = minBTCPayVersion
                });
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
                    state = "queued",
                    buildInfo = bi.ToString()
                });
        }
    }
}
