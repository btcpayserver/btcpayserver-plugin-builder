using Dapper;
using Newtonsoft.Json.Linq;

namespace PluginBuilder.Services;

public class AdminEventService(DBConnectionFactory connections)
{
    public static readonly IReadOnlyList<string> EventTypes = Array.AsReadOnly(new[]
    {
        "user.registered", "user.github_verified", "user.first_build_triggered",
        "build.triggered", "build.succeeded", "build.failed", "listing.requested", "listing.approved", "listing.rejected",
        "admin.token_created", "admin.token_revoked"
    });

    public async Task<EventPage> Read(long after, int limit, string? type, CancellationToken cancellationToken)
    {
        await using var conn = await connections.Open(cancellationToken);
        var rows = (await conn.QueryAsync<EventRow>(new CommandDefinition("""
            SELECT id, type, created_at AS CreatedAt, data::text AS Data FROM admin_events
            WHERE id > @after AND (@type IS NULL OR type = @type)
            ORDER BY id LIMIT @take
            """, new { after, type, take = limit + 1 }, cancellationToken: cancellationToken))).ToList();
        var events = rows.Take(limit).Select(ToJson).ToArray();
        return new EventPage(events, events.Length == 0 ? after : rows[events.Length - 1].Id, rows.Count > limit);
    }

    public static JObject ToJson(EventRow row) => new()
    {
        ["id"] = row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["type"] = row.Type,
        ["schemaVersion"] = 1,
        ["createdAt"] = row.CreatedAt,
        ["data"] = JObject.Parse(row.Data)
    };

    public sealed record EventPage(JObject[] Events, long NextCursor, bool HasMore);
    public class EventRow
    {
        public long Id { get; set; }
        public string Type { get; set; } = "";
        public DateTimeOffset CreatedAt { get; set; }
        public string Data { get; set; } = "{}";
    }
}
