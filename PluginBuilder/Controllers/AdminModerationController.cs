using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Newtonsoft.Json.Linq;
using Npgsql;
using PluginBuilder.Authentication;
using PluginBuilder.DataModels;
using PluginBuilder.Events;
using PluginBuilder.ModelBinders;
using PluginBuilder.Services;
using PluginBuilder.Util;

namespace PluginBuilder.Controllers;

/// <summary>
/// Containment for the admin agent: lock an account, stop a build, change a plugin's visibility and revoke a published
/// version. Every action needs a reason and emits an admin
/// event in the same transaction as the change, so a rollback leaves neither behind.
/// </summary>
[ApiController]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/v1/admin")]
[Authorize(Roles = Roles.ServerAdmin, AuthenticationSchemes = PluginBuilderAuthenticationSchemes.AdminApi)]
public class AdminModerationController(DBConnectionFactory connections, AccountModerationService moderation, EventAggregator events,
    IOutputCacheStore outputCache, GithubIdentityService githubIdentities)
    : ControllerBase
{
    public const string CancelledByAdmin = AccountModerationService.CancelledByAdmin;
    public const string CancelledByLock = AccountModerationService.CancelledByLock;

    /// <summary>The account's admin lock, or null; failed-login lockouts show only in lockoutEnd.</summary>
    public const string AdminLockJson = """
        (SELECT jsonb_build_object('until', l.locked_until, 'reason', l.reason, 'lockedBy', l.locked_by, 'lockedAt', l.locked_at)
         FROM admin_account_locks l WHERE l.user_id = u."Id" AND l.locked_until > CURRENT_TIMESTAMP)
        """;

    [HttpPost("users/{userId}/burn")]
    public async Task<IActionResult> Burn(string userId, ReasonRequest request, CancellationToken cancellationToken)
    {
        var result = await githubIdentities.Burn(userId, request.Reason.Trim(), User.FindFirstValue(ClaimTypes.NameIdentifier),
            User.FindFirstValue(PluginBuilderAuthenticationSchemes.TokenIdClaim), cancellationToken);
        if (result is null)
            return NotFound();
        if (result.SkippedAdminUserIds.Contains(userId))
            return Conflict(new { message = "Server admin accounts cannot be burned through the API." });
        return Ok(result);
    }

    [HttpGet("github-bans")]
    public async Task<IActionResult> GithubBans(CancellationToken cancellationToken)
    {
        await using var conn = await connections.Open(cancellationToken);
        var rows = await conn.QueryAsync<string>(new CommandDefinition("""
            SELECT jsonb_build_object('id', id, 'githubUserId', github_user_id, 'githubLogin', github_login, 'reason', reason,
              'bannedBy', banned_by, 'bannedAt', banned_at)::text FROM github_bans ORDER BY id
            """, cancellationToken: cancellationToken));
        return Ok(new JArray(rows.Select(JObject.Parse)));
    }

    [HttpPost("github-bans/{banId:long}/revoke")]
    public async Task<IActionResult> RevokeGithubBan(long banId, ReasonRequest request, CancellationToken cancellationToken)
    {
        if (banId <= 0)
            return BadRequest(new { message = "banId must be positive." });
        return await githubIdentities.Revoke(banId, request.Reason.Trim(), User.FindFirstValue(ClaimTypes.NameIdentifier),
            User.FindFirstValue(PluginBuilderAuthenticationSchemes.TokenIdClaim), cancellationToken)
            ? Ok(new { banId, revoked = true }) : NotFound();
    }

    [HttpPost("users/{userId}/lock")]
    public async Task<IActionResult> Lock(string userId, LockRequest request, CancellationToken cancellationToken)
    {
        if (request.Until is { } until && until <= DateTimeOffset.UtcNow)
            return BadRequest(new { message = "until must be in the future; omit it for an indefinite lock." });

        await using var conn = await connections.Open(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);
        var target = await conn.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition("""
            SELECT u."Id" AS Id,
              EXISTS(SELECT 1 FROM "AspNetUserRoles" ur JOIN "AspNetRoles" r ON r."Id" = ur."RoleId"
                     WHERE ur."UserId" = u."Id" AND r."Name" = 'ServerAdmin') AS IsAdmin
            FROM "AspNetUsers" u WHERE u."Id" = @userId FOR UPDATE OF u
            """, new { userId }, tx, cancellationToken: cancellationToken));
        if (target is null)
            return NotFound();
        if (target.IsAdmin)
            return Conflict(new { message = "Server admin accounts cannot be locked through the API." });

        var unfinished = await moderation.LockAccount(conn, tx, userId, request.Until, request.Reason,
            User.FindFirstValue(ClaimTypes.NameIdentifier), User.FindFirstValue(PluginBuilderAuthenticationSchemes.TokenIdClaim), cancellationToken);
        await tx.CommitAsync(cancellationToken);
        moderation.Stop(unfinished, CancelledByLock);
        return await ReadUser(conn, userId, cancellationToken);
    }

    [HttpPost("users/{userId}/unlock")]
    public async Task<IActionResult> Unlock(string userId, ReasonRequest request, CancellationToken cancellationToken)
    {
        await using var conn = await connections.Open(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);
        var target = await conn.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition("""
            SELECT u."Id" AS Id, COALESCE(u."LockoutEnd" > CURRENT_TIMESTAMP, FALSE) AS LockedOut,
              EXISTS(SELECT 1 FROM admin_account_locks l WHERE l.user_id = u."Id" AND l.locked_until > CURRENT_TIMESTAMP) AS AdminLocked
            FROM "AspNetUsers" u WHERE u."Id" = @userId FOR UPDATE OF u
            """, new { userId }, tx, cancellationToken: cancellationToken));
        if (target is null)
            return NotFound();
        // Lifts an admin lock, and also a failed-login lockout an admin wants to end early.
        if (!target.AdminLocked && !target.LockedOut)
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
        await using var conn = await connections.Open(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);
        var state = await conn.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT state FROM builds WHERE plugin_slug = @pluginSlug AND id = @buildId FOR UPDATE",
            new { pluginSlug, buildId }, tx, cancellationToken: cancellationToken));
        if (state is null)
            return NotFound();
        if (BuildStatesExtensions.TerminalEventNames.Contains(state))
            return Conflict(new { message = "This build has already finished." });

        var cancelled = new List<AccountModerationService.BuildRow> { new(pluginSlug, buildId, state) };
        await moderation.Cancel(conn, tx, cancelled, CancelledByAdmin, request.Reason.Trim(),
            User.FindFirstValue(ClaimTypes.NameIdentifier), User.FindFirstValue(PluginBuilderAuthenticationSchemes.TokenIdClaim), cancellationToken);
        await tx.CommitAsync(cancellationToken);
        moderation.Stop(cancelled, CancelledByAdmin);
        var rows = await conn.QueryAsync<string>(new CommandDefinition("""
            SELECT jsonb_build_object('buildId', id, 'pluginSlug', plugin_slug, 'state', state, 'buildInfo', build_info)::text
            FROM builds WHERE plugin_slug = @pluginSlug AND id = @buildId
            """, new { pluginSlug, buildId }, cancellationToken: cancellationToken));
        return Ok(JObject.Parse(rows.Single()));
    }

    /// <summary>The same change as the admin edit page, with a reason and an event. Hidden and unlisted plugins leave the
    /// public listing and hidden ones leave update checks; direct version links keep working, so revoke a version to stop
    /// it being served.</summary>
    [HttpPut("plugins/{pluginSlug}/visibility")]
    public async Task<IActionResult> SetVisibility(string pluginSlug, VisibilityRequest request, CancellationToken cancellationToken)
    {
        var visibility = request.Visibility.Trim().ToLowerInvariant();
        if (!Visibilities.Contains(visibility))
            return BadRequest(new { message = "visibility must be hidden, unlisted or listed." });

        await using var conn = await connections.Open(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);
        var current = await conn.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT visibility::text FROM plugins WHERE slug = @pluginSlug FOR UPDATE",
            new { pluginSlug }, tx, cancellationToken: cancellationToken));
        if (current is null)
            return NotFound();
        if (current == visibility)
            return Conflict(new { message = $"This plugin is already {current}." });

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE plugins SET visibility = @visibility::plugin_visibility_enum WHERE slug = @pluginSlug",
            new { pluginSlug, visibility }, tx, cancellationToken: cancellationToken));
        await Emit(conn, tx, "plugin.visibility_changed", new JObject
        {
            ["pluginSlug"] = pluginSlug, ["previousVisibility"] = current, ["visibility"] = visibility, ["reason"] = request.Reason.Trim()
        }, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        await outputCache.EvictByTagAsync(CacheTags.Plugins, cancellationToken);
        return Ok(new JObject { ["pluginSlug"] = pluginSlug, ["previousVisibility"] = current, ["visibility"] = visibility });
    }

    /// <summary>Stops serving one published version: the owner's Remove, done by an admin with a reason and an event. The
    /// build is kept and marked removed; the version cannot be restored, only published again from a new build, so lock
    /// the account as well when the owner is the problem.</summary>
    [HttpPost("plugins/{pluginSlug}/versions/{version}/revoke")]
    public async Task<IActionResult> RevokeVersion(string pluginSlug, [ModelBinder(typeof(PluginVersionModelBinder))] PluginVersion version,
        ReasonRequest request, CancellationToken cancellationToken)
    {
        await using var conn = await connections.Open(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);
        var buildId = await conn.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            "SELECT build_id FROM versions WHERE plugin_slug = @pluginSlug AND ver = @ver",
            new { pluginSlug, ver = version.VersionParts }, tx, cancellationToken: cancellationToken));
        if (buildId is null)
            return NotFound();

        var build = new FullBuildId(new PluginSlug(pluginSlug), buildId.Value);
        // Match owner removal and plugin deletion: lock the build before deleting its version.
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE builds SET state = 'removed' WHERE plugin_slug = @pluginSlug AND id = @buildId",
            new { pluginSlug, buildId = buildId.Value }, tx, cancellationToken: cancellationToken));
        var removed = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM versions WHERE plugin_slug = @pluginSlug AND ver = @ver AND build_id = @buildId",
            new { pluginSlug, ver = version.VersionParts, buildId = buildId.Value }, tx, cancellationToken: cancellationToken));
        if (removed == 0)
            return NotFound(); // Roll back the build update if this version was removed or replaced while we waited.
        await Emit(conn, tx, "version.revoked", new JObject
        {
            ["pluginSlug"] = pluginSlug, ["version"] = version.ToString(), ["buildId"] = buildId.Value, ["reason"] = request.Reason.Trim()
        }, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        events.Publish(new BuildChanged(build, BuildStates.Removed));
        await outputCache.EvictByTagAsync(CacheTags.Plugins, cancellationToken);
        return Ok(new JObject { ["pluginSlug"] = pluginSlug, ["version"] = version.ToString(), ["buildId"] = buildId.Value, ["revoked"] = true });
    }

    private Task Emit(NpgsqlConnection conn, NpgsqlTransaction tx, string type, JObject data, CancellationToken cancellationToken) =>
        moderation.Emit(conn, tx, type, data, User.FindFirstValue(ClaimTypes.NameIdentifier),
            User.FindFirstValue(PluginBuilderAuthenticationSchemes.TokenIdClaim), cancellationToken);

    private async Task<IActionResult> ReadUser(NpgsqlConnection conn, string userId, CancellationToken cancellationToken)
    {
        var json = await conn.QuerySingleAsync<string>(new CommandDefinition($"""
            SELECT jsonb_build_object('id', u."Id", 'email', u."Email", 'lockoutEnabled', u."LockoutEnabled", 'lockoutEnd', u."LockoutEnd",
              'adminLock', {AdminLockJson})::text
            FROM "AspNetUsers" u WHERE u."Id" = @userId
            """, new { userId }, cancellationToken: cancellationToken));
        return Ok(JObject.Parse(json));
    }

    private sealed class UserRow
    {
        public string Id { get; set; } = "";
        public bool LockedOut { get; set; }
        public bool IsAdmin { get; set; }
        public bool AdminLocked { get; set; }
    }

    public class ReasonRequest
    {
        [Required, MaxLength(1000)] public string Reason { get; set; } = "";
    }

    private static readonly HashSet<string> Visibilities = ["hidden", "unlisted", "listed"];

    public sealed class VisibilityRequest : ReasonRequest
    {
        [Required, MaxLength(20)] public string Visibility { get; set; } = "";
    }

    public sealed class LockRequest : ReasonRequest
    {
        public DateTimeOffset? Until { get; set; }
    }
}
