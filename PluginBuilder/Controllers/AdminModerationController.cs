using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Npgsql;
using PluginBuilder.Authentication;
using PluginBuilder.Events;
using PluginBuilder.Services;
using PluginBuilder.Util;

namespace PluginBuilder.Controllers;

/// <summary>
/// Containment for the admin agent: lock an account and stop a build. Every action needs a reason and emits an admin
/// event in the same transaction as the change, so a rollback leaves neither behind.
/// </summary>
[ApiController]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/v1/admin")]
[Authorize(Roles = Roles.ServerAdmin, AuthenticationSchemes = PluginBuilderAuthenticationSchemes.AdminApi)]
public class AdminModerationController(DBConnectionFactory connections, BuildCancellationRegistry cancellations, EventAggregator events)
    : ControllerBase
{
    public const string CancelledByAdmin = "Cancelled by a server admin.";
    public const string CancelledByLock = "Cancelled: the account that started this build was locked by a server admin.";

    /// <summary>The account's admin lock, or null; failed-login lockouts show only in lockoutEnd.</summary>
    public const string AdminLockJson = """
        (SELECT jsonb_build_object('until', l.locked_until, 'reason', l.reason, 'lockedBy', l.locked_by, 'lockedAt', l.locked_at)
         FROM admin_account_locks l WHERE l.user_id = u."Id" AND l.locked_until > CURRENT_TIMESTAMP)
        """;

    [HttpPost("users/{userId}/lock")]
    public async Task<IActionResult> Lock(string userId, LockRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest(new { message = "Give a nonempty reason." });
        if (request.Until is { } until && until <= DateTimeOffset.UtcNow)
            return BadRequest(new { message = "until must be in the future; omit it for an indefinite lock." });

        await using var conn = await connections.Open(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);
        var target = await conn.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition("""
            SELECT u."Id" AS Id, u."LockoutEnd" AS LockoutEnd,
              EXISTS(SELECT 1 FROM "AspNetUserRoles" ur JOIN "AspNetRoles" r ON r."Id" = ur."RoleId"
                     WHERE ur."UserId" = u."Id" AND r."Name" = 'ServerAdmin') AS IsAdmin
            FROM "AspNetUsers" u WHERE u."Id" = @userId FOR UPDATE OF u
            """, new { userId }, tx, cancellationToken: cancellationToken));
        if (target is null)
            return NotFound();
        if (target.IsAdmin)
            return Conflict(new { message = "Server admin accounts cannot be locked through the API." });

        // The new security stamp ends the account's sessions at their next validation and invalidates any token bound to it.
        // Identity's lockout refuses sign-in and tokens; the admin lock row is what suspends the account's builds,
        // since failed logins set Identity's lockout too.
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE "AspNetUsers" SET "LockoutEnabled" = TRUE, "LockoutEnd" = @end,
              "SecurityStamp" = @stamp, "ConcurrencyStamp" = @concurrency
            WHERE "Id" = @userId;
            INSERT INTO admin_account_locks (user_id, locked_until, reason, locked_by) VALUES (@userId, @end, @reason, @by)
            ON CONFLICT (user_id) DO UPDATE SET locked_until = EXCLUDED.locked_until, reason = EXCLUDED.reason,
              locked_by = EXCLUDED.locked_by, locked_at = CURRENT_TIMESTAMP
            """, new
        {
            userId, end = request.Until ?? DateTimeOffset.MaxValue, reason = request.Reason.Trim(),
            by = User.FindFirstValue(ClaimTypes.NameIdentifier),
            stamp = Guid.NewGuid().ToString("N").ToUpperInvariant(), concurrency = Guid.NewGuid().ToString()
        }, tx, cancellationToken: cancellationToken));
        var unfinished = (await conn.QueryAsync<BuildRow>(new CommandDefinition("""
            SELECT plugin_slug AS PluginSlug, id AS BuildId, state AS State FROM builds
            WHERE triggered_by = @userId AND state <> ALL(@terminal) ORDER BY plugin_slug, id FOR UPDATE
            """, new { userId, terminal = BuildStatesExtensions.TerminalEventNames }, tx, cancellationToken: cancellationToken))).ToList();
        await Emit(conn, tx, "user.locked", new JObject
        {
            ["userId"] = userId, ["reason"] = request.Reason.Trim(), ["until"] = request.Until,
            ["cancelledBuilds"] = new JArray(unfinished.Select(b => new JObject { ["pluginSlug"] = b.PluginSlug, ["buildId"] = b.BuildId }))
        }, cancellationToken);
        await Cancel(conn, tx, unfinished, CancelledByLock, "Account locked: " + request.Reason.Trim(), cancellationToken);
        await tx.CommitAsync(cancellationToken);
        var cancelled = unfinished;
        Stop(cancelled, CancelledByLock);
        return await ReadUser(conn, userId, cancellationToken);
    }

    [HttpPost("users/{userId}/unlock")]
    public async Task<IActionResult> Unlock(string userId, ReasonRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest(new { message = "Give a nonempty reason." });

        await using var conn = await connections.Open(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);
        var target = await conn.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition("""
            SELECT u."Id" AS Id, u."LockoutEnd" AS LockoutEnd,
              EXISTS(SELECT 1 FROM admin_account_locks l WHERE l.user_id = u."Id" AND l.locked_until > CURRENT_TIMESTAMP) AS AdminLocked
            FROM "AspNetUsers" u WHERE u."Id" = @userId FOR UPDATE OF u
            """, new { userId }, tx, cancellationToken: cancellationToken));
        if (target is null)
            return NotFound();
        // Lifts an admin lock, and also a failed-login lockout an admin wants to end early.
        if (!target.AdminLocked && (target.LockoutEnd is not { } end || end <= DateTimeOffset.UtcNow))
            return Conflict(new { message = "This account is not locked." });

        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE "AspNetUsers" SET "LockoutEnd" = NULL, "AccessFailedCount" = 0, "ConcurrencyStamp" = @concurrency WHERE "Id" = @userId;
            DELETE FROM admin_account_locks WHERE user_id = @userId
            """, new { userId, concurrency = Guid.NewGuid().ToString() }, tx, cancellationToken: cancellationToken));
        await Emit(conn, tx, "user.unlocked", new JObject { ["userId"] = userId, ["reason"] = request.Reason.Trim() }, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return await ReadUser(conn, userId, cancellationToken);
    }

    [HttpPost("plugins/{pluginSlug}/builds/{buildId:long}/cancel")]
    public async Task<IActionResult> CancelBuild(string pluginSlug, long buildId, ReasonRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest(new { message = "Give a nonempty reason." });

        await using var conn = await connections.Open(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);
        var state = await conn.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT state FROM builds WHERE plugin_slug = @pluginSlug AND id = @buildId FOR UPDATE",
            new { pluginSlug, buildId }, tx, cancellationToken: cancellationToken));
        if (state is null)
            return NotFound();
        if (BuildStatesExtensions.TerminalEventNames.Contains(state))
            return Conflict(new { message = "This build has already finished." });

        var cancelled = new List<BuildRow> { new() { PluginSlug = pluginSlug, BuildId = buildId, State = state } };
        await Cancel(conn, tx, cancelled, CancelledByAdmin, request.Reason.Trim(), cancellationToken);
        await tx.CommitAsync(cancellationToken);
        Stop(cancelled, CancelledByAdmin);
        var rows = await conn.QueryAsync<string>(new CommandDefinition("""
            SELECT jsonb_build_object('buildId', id, 'pluginSlug', plugin_slug, 'state', state, 'buildInfo', build_info)::text
            FROM builds WHERE plugin_slug = @pluginSlug AND id = @buildId
            """, new { pluginSlug, buildId }, cancellationToken: cancellationToken));
        return Ok(JObject.Parse(rows.Single()));
    }

    /// <summary>Marks each locked build row failed with <paramref name="error"/>; build.cancelled precedes the trigger's build.failed.</summary>
    private async Task Cancel(NpgsqlConnection conn, NpgsqlTransaction tx, List<BuildRow> builds, string error, string reason,
        CancellationToken cancellationToken)
    {
        foreach (var build in builds)
        {
            await Emit(conn, tx, "build.cancelled", new JObject
            {
                ["pluginSlug"] = build.PluginSlug, ["buildId"] = build.BuildId, ["previousState"] = build.State, ["reason"] = reason
            }, cancellationToken);
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE builds SET state = 'failed', build_info = COALESCE(build_info, '{}'::jsonb) || jsonb_build_object('error', @error::text)
                WHERE plugin_slug = @pluginSlug AND id = @buildId
                """, new { build.PluginSlug, build.BuildId, error }, tx, cancellationToken: cancellationToken));
        }
    }

    private void Stop(IEnumerable<BuildRow> builds, string error)
    {
        foreach (var build in builds)
        {
            var id = new FullBuildId(new PluginSlug(build.PluginSlug), build.BuildId);
            cancellations.Cancel(id);
            events.Publish(new BuildChanged(id, BuildStates.Failed) { BuildInfo = new JObject { ["error"] = error }.ToString() });
        }
    }

    private Task Emit(NpgsqlConnection conn, NpgsqlTransaction tx, string type, JObject data, CancellationToken cancellationToken)
    {
        // The acting account and token, so the stream shows who contained what and through which delegation.
        data["by"] = User.FindFirstValue(ClaimTypes.NameIdentifier);
        data["tokenId"] = User.FindFirstValue(PluginBuilderAuthenticationSchemes.TokenIdClaim);
        return conn.ExecuteAsync(new CommandDefinition("SELECT emit_admin_event(@type, @data::jsonb)",
            new { type, data = data.ToString(Newtonsoft.Json.Formatting.None) }, tx, cancellationToken: cancellationToken));
    }

    private async Task<IActionResult> ReadUser(NpgsqlConnection conn, string userId, CancellationToken cancellationToken)
    {
        var json = await conn.QuerySingleAsync<string>(new CommandDefinition($"""
            SELECT jsonb_build_object('id', u."Id", 'email', u."Email", 'lockoutEnabled', u."LockoutEnabled", 'lockoutEnd', u."LockoutEnd",
              'adminLock', {AdminLockJson})::text
            FROM "AspNetUsers" u WHERE u."Id" = @userId
            """, new { userId }, cancellationToken: cancellationToken));
        return Ok(JObject.Parse(json));
    }

    private sealed class BuildRow
    {
        public string PluginSlug { get; set; } = "";
        public long BuildId { get; set; }
        public string State { get; set; } = "";
    }

    private sealed class UserRow
    {
        public string Id { get; set; } = "";
        public DateTimeOffset? LockoutEnd { get; set; }
        public bool IsAdmin { get; set; }
        public bool AdminLocked { get; set; }
    }

    public class ReasonRequest
    {
        [Required, MaxLength(1000)] public string Reason { get; set; } = "";
    }

    public sealed class LockRequest : ReasonRequest
    {
        public DateTimeOffset? Until { get; set; }
    }
}
