using Dapper;
using Newtonsoft.Json.Linq;
using Npgsql;
using PluginBuilder.Events;
using PluginBuilder.Util;

namespace PluginBuilder.Services;

public sealed class AccountModerationService(BuildCancellationRegistry cancellations, EventAggregator events)
{
    public const string CancelledByAdmin = "Cancelled by a server admin.";
    public const string CancelledByLock = "Cancelled: the account that started this build was locked by a server admin.";

    // Serialize ban changes and account containment against each other.
    public static Task Serialize(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(734121, 1)", transaction: tx, cancellationToken: ct));

    // The caller locks the user row and excludes server admins. Changes and events share its transaction.
    public async Task<List<BuildRow>> LockAccount(NpgsqlConnection conn, NpgsqlTransaction tx, string userId,
        DateTimeOffset? until, string reason, string? actor, string? tokenId, CancellationToken cancellationToken)
    {
        reason = reason.Trim();
        // The new security stamp ends the account's sessions at their next validation and invalidates any token bound to it.
        // Identity's lockout refuses sign-in. The admin lock row suspends builds and publisher tokens,
        // since failed logins set Identity's lockout too.
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE "AspNetUsers" SET "LockoutEnabled" = TRUE, "LockoutEnd" = @end,
              "SecurityStamp" = @stamp, "ConcurrencyStamp" = @concurrency
            WHERE "Id" = @userId;
            DELETE FROM "AspNetUserTokens" t USING "AspNetUsers" u
            WHERE t."UserId" = u."Id" AND u."Id" = @userId AND NOT u."TwoFactorEnabled"
              AND t."LoginProvider" = '[AspNetUserStore]' AND t."Name" = 'AuthenticatorKey';
            INSERT INTO admin_account_locks (user_id, locked_until, reason, locked_by) VALUES (@userId, @end, @reason, @by)
            ON CONFLICT (user_id) DO UPDATE SET locked_until = EXCLUDED.locked_until, reason = EXCLUDED.reason,
              locked_by = EXCLUDED.locked_by, locked_at = CURRENT_TIMESTAMP
            """, new
        {
            userId, end = until?.ToUniversalTime() ?? DateTimeOffset.MaxValue, reason = reason,
            by = actor,
            stamp = Guid.NewGuid().ToString("N").ToUpperInvariant(), concurrency = Guid.NewGuid().ToString()
        }, tx, cancellationToken: cancellationToken));
        var unfinished = (await conn.QueryAsync<BuildRow>(new CommandDefinition("""
            SELECT plugin_slug AS PluginSlug, id AS BuildId, state AS State FROM builds
            WHERE triggered_by = @userId AND state <> ALL(@terminal) ORDER BY plugin_slug, id FOR UPDATE
            """, new { userId, terminal = BuildStatesExtensions.TerminalEventNames }, tx, cancellationToken: cancellationToken))).ToList();
        await Emit(conn, tx, "user.locked", new JObject
        {
            ["userId"] = userId, ["reason"] = reason, ["until"] = until,
            ["cancelledBuilds"] = new JArray(unfinished.Select(b => new JObject { ["pluginSlug"] = b.PluginSlug, ["buildId"] = b.BuildId }))
        }, actor, tokenId, cancellationToken);
        await Cancel(conn, tx, unfinished, CancelledByLock, "Account locked: " + reason, actor, tokenId, cancellationToken);
        return unfinished;
    }

    /// <summary>Marks each locked build row failed with <paramref name="error"/>; build.cancelled precedes the trigger's build.failed.</summary>
    public async Task Cancel(NpgsqlConnection conn, NpgsqlTransaction tx, IReadOnlyList<BuildRow> builds, string error, string reason, string? actor, string? tokenId,
        CancellationToken cancellationToken)
    {
        foreach (var build in builds)
        {
            await Emit(conn, tx, "build.cancelled", new JObject
            {
                ["pluginSlug"] = build.PluginSlug, ["buildId"] = build.BuildId, ["previousState"] = build.State, ["reason"] = reason
            }, actor, tokenId, cancellationToken);
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE builds SET state = 'failed', build_info = COALESCE(build_info, '{}'::jsonb) || jsonb_build_object('error', @error::text)
                WHERE plugin_slug = @pluginSlug AND id = @buildId
                """, new { build.PluginSlug, build.BuildId, error }, tx, cancellationToken: cancellationToken));
        }
    }

    public void Stop(IEnumerable<BuildRow> builds, string error)
    {
        foreach (var build in builds)
        {
            var id = new FullBuildId(new PluginSlug(build.PluginSlug), build.BuildId);
            cancellations.Cancel(id);
            events.Publish(new BuildChanged(id, BuildStates.Failed) { BuildInfo = new JObject { ["error"] = error }.ToString() });
        }
    }

    public Task Emit(NpgsqlConnection conn, NpgsqlTransaction tx, string type, JObject data, string? actor, string? tokenId, CancellationToken cancellationToken)
    {
        // The acting account and token, so the stream shows who contained what and through which delegation.
        data["by"] = actor;
        data["tokenId"] = tokenId;
        return conn.ExecuteAsync(new CommandDefinition("SELECT emit_admin_event(@type, @data::jsonb)",
            new { type, data = data.ToString(Newtonsoft.Json.Formatting.None) }, tx, cancellationToken: cancellationToken));
    }

    public sealed record BuildRow(string PluginSlug, long BuildId, string State);
}
