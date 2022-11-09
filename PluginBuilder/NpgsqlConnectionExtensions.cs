using Dapper;
using Newtonsoft.Json.Linq;
using Npgsql;

namespace PluginBuilder
{
    public static class NpgsqlConnectionExtensions
    {
        public static async Task NewPlugin(this NpgsqlConnection connection, PluginSlug pluginSlug)
        {
            await connection.ExecuteAsync("INSERT INTO plugins (slug) VALUES (@id);",
                new
                {
                    id = pluginSlug.ToString(),
                });
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
        public static Task<long> NewBuild(this NpgsqlConnection connection, PluginSlug pluginSlug)
        {
            return connection.ExecuteScalarAsync<long>("" +
                "WITH cte AS " +
                "( " +
                " INSERT INTO builds_ids AS bi VALUES (@plugin_slug, 0)" +
                "        ON CONFLICT (plugin_slug) DO UPDATE SET curr_id=bi.curr_id+1 " +
                " RETURNING curr_id " +
                ") " +
                "INSERT INTO builds (plugin_slug, id, state) VALUES (@plugin_slug, (SELECT * FROM cte), @state) RETURNING id;",
                new
                {
                    plugin_slug = pluginSlug.ToString(),
                    state = "queued"
                });
        }
    }
}
