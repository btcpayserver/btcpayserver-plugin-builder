using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using PluginBuilder.Authentication;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.Services;
using PluginBuilder.Util;

namespace PluginBuilder.Controllers;

[ApiController]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/v1/admin")]
[Authorize(Roles = Roles.ServerAdmin, AuthenticationSchemes = PluginBuilderAuthenticationSchemes.AdminApi)]
public class AdminReviewController(DBConnectionFactory connections, ListingReviewService reviews, AdminSettingsCache settings) : ControllerBase
{
    private const string UserJson = """
        jsonb_build_object('id', u."Id", 'email', u."Email", 'createdAt', u."CreatedAt",
          'emailVerified', u."EmailConfirmed", 'githubVerified', NULLIF(u."GithubGistUrl", '') IS NOT NULL,
          'github', u."AccountDetail"->>'github', 'githubProofUrl', u."GithubGistUrl",
          'nostrNpub', u."AccountDetail"->'nostr'->>'npub', 'nostrProof', u."AccountDetail"->'nostr'->>'proof',
          'lockoutEnabled', u."LockoutEnabled", 'lockoutEnd', u."LockoutEnd", 'twoFactorEnabled', u."TwoFactorEnabled",
          'roles', COALESCE((SELECT jsonb_agg(r."Name" ORDER BY r."Name") FROM "AspNetUserRoles" ur
            JOIN "AspNetRoles" r ON r."Id" = ur."RoleId" WHERE ur."UserId" = u."Id"), '[]'::jsonb))
        """;
    private const string PluginJson = """
        jsonb_build_object('slug', p.slug, 'identifier', p.identifier, 'visibility', p.visibility,
          'createdAt', p.added_at, 'settings', p.settings,
          'owners', COALESCE((SELECT jsonb_agg(jsonb_build_object('userId', up.user_id, 'isPrimary', up.is_primary_owner) ORDER BY up.user_id)
             FROM users_plugins up WHERE up.plugin_slug = p.slug), '[]'::jsonb))
        """;
    private const string BuildJson = """
        jsonb_build_object('buildId', b.id, 'pluginSlug', b.plugin_slug, 'state', b.state, 'createdAt', b.created_at,
          'triggeredBy', b.triggered_by, 'buildInfo', b.build_info, 'manifestInfo', b.manifest_info,
          'versions', COALESCE((SELECT jsonb_agg(jsonb_build_object('version', array_to_string(v.ver, '.'), 'preRelease', v.pre_release))
             FROM versions v WHERE v.plugin_slug = b.plugin_slug AND v.build_id = b.id), '[]'::jsonb))
        """;
    private const string ListingJson = """
        jsonb_build_object('id', lr.id, 'pluginSlug', lr.plugin_slug, 'status', lr.status, 'submittedAt', lr.submitted_at,
          'submittedBy', lr.submitted_by, 'releaseNote', lr.release_note, 'telegramVerificationMessage', lr.telegram_verification_message,
          'userReviews', lr.user_reviews, 'announcementDate', lr.announcement_date, 'reviewedAt', lr.reviewed_at,
          'reviewedBy', lr.reviewed_by, 'reviewedByToken', lr.reviewed_by_token, 'reviewNote', lr.review_note, 'rejectionReason', lr.rejection_reason)
        """;

    [HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken cancellationToken) =>
        await UserDetail(User.FindFirstValue(ClaimTypes.NameIdentifier)!, cancellationToken);

    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken cancellationToken)
    {
        await using var conn = await connections.Open(cancellationToken);
        var counts = await conn.QuerySingleAsync(new CommandDefinition("""
            SELECT (SELECT count(*) FROM "AspNetUsers") AS users,
              (SELECT count(*) FROM plugins) AS plugins,
              (SELECT count(*) FROM plugin_listing_requests WHERE status = 'pending') AS "pendingListingRequests",
              (SELECT count(*) FROM builds WHERE state NOT IN ('uploaded', 'failed', 'removed')) AS "activeBuilds",
              (SELECT count(*) FROM admin_event_deliveries WHERE status = 'failed') AS "failedEventDeliveries"
            """, cancellationToken: cancellationToken));
        return Ok(new { counts, registrationEnabled = settings.RegistrationEnabled, newBuildsEnabled = settings.NewBuildsEnabled });
    }

    [HttpGet("users")]
    public async Task<IActionResult> Users([FromQuery, MaxLength(256)] string after = "", [FromQuery, Range(1, 100)] int limit = 50,
        [FromQuery, MaxLength(256)] string? email = null, CancellationToken cancellationToken = default)
    {
        var rows = await ReadJson($"""
            SELECT ({UserJson})::text FROM "AspNetUsers" u
            WHERE u."Id" > @after AND (@email IS NULL OR lower(u."Email") = lower(@email))
            ORDER BY u."Id" LIMIT @take
            """, new { after, email, take = limit + 1 }, cancellationToken);
        return Page(rows, limit, "id", after);
    }

    [HttpGet("users/{userId}")]
    public async Task<IActionResult> UserDetail(string userId, CancellationToken cancellationToken)
    {
        var rows = await ReadJson($"SELECT ({UserJson})::text FROM \"AspNetUsers\" u WHERE u.\"Id\" = @userId", new { userId }, cancellationToken);
        return rows.Count == 0 ? NotFound() : Ok(rows[0]);
    }

    [HttpGet("plugins")]
    public async Task<IActionResult> Plugins([FromQuery, MaxLength(256)] string after = "", [FromQuery, Range(1, 100)] int limit = 50,
        [FromQuery, MaxLength(256)] string? userId = null, CancellationToken cancellationToken = default)
    {
        var rows = await ReadJson($"""
            SELECT ({PluginJson})::text FROM plugins p WHERE p.slug > @after
            AND (@userId IS NULL OR EXISTS (SELECT 1 FROM users_plugins up WHERE up.plugin_slug = p.slug AND up.user_id = @userId))
            ORDER BY p.slug LIMIT @take
            """, new { after, userId, take = limit + 1 }, cancellationToken);
        return Page(rows, limit, "slug", after);
    }

    [HttpGet("plugins/{pluginSlug}")]
    public async Task<IActionResult> Plugin(string pluginSlug, CancellationToken cancellationToken)
    {
        var rows = await ReadJson($"SELECT ({PluginJson})::text FROM plugins p WHERE p.slug = @pluginSlug", new { pluginSlug }, cancellationToken);
        return rows.Count == 0 ? NotFound() : Ok(rows[0]);
    }

    [HttpGet("plugins/{pluginSlug}/builds")]
    public async Task<IActionResult> Builds(string pluginSlug, [FromQuery, Range(0, long.MaxValue)] long? before = null,
        [FromQuery, Range(1, 100)] int limit = 50, CancellationToken cancellationToken = default)
    {
        var rows = await ReadJson($"""
            SELECT ({BuildJson})::text FROM builds b WHERE b.plugin_slug = @pluginSlug AND (@before IS NULL OR b.id < @before)
            ORDER BY b.id DESC LIMIT @take
            """, new { pluginSlug, before, take = limit + 1 }, cancellationToken);
        return Page(rows, limit, "buildId", before?.ToString());
    }

    [HttpGet("plugins/{pluginSlug}/builds/{buildId:long}")]
    public async Task<IActionResult> Build(string pluginSlug, long buildId, CancellationToken cancellationToken)
    {
        var rows = await ReadJson($"SELECT ({BuildJson})::text FROM builds b WHERE b.plugin_slug = @pluginSlug AND b.id = @buildId",
            new { pluginSlug, buildId }, cancellationToken);
        return rows.Count == 0 ? NotFound() : Ok(rows[0]);
    }

    [HttpGet("plugins/{pluginSlug}/builds/{buildId:long}/logs")]
    public async Task<IActionResult> Logs(string pluginSlug, long buildId, [FromQuery, Range(1, long.MaxValue)] long? before = null,
        [FromQuery, Range(1, 100)] int limit = 50, CancellationToken cancellationToken = default)
    {
        var rows = await ReadJson("""
            SELECT jsonb_build_object('id', id::text, 'createdAt', created_at, 'text', left(logs, 16384), 'truncated', length(logs) > 16384)::text
            FROM builds_logs WHERE plugin_slug = @pluginSlug AND build_id = @buildId AND (@before IS NULL OR id < @before)
            ORDER BY id DESC LIMIT @take
            """, new { pluginSlug, buildId, before, take = limit + 1 }, cancellationToken);
        return Page(rows, limit, "id", before?.ToString());
    }

    [HttpGet("plugins/{pluginSlug}/builds/{buildId:long}/logs/{logId:long}")]
    public async Task<IActionResult> LogChunk(string pluginSlug, long buildId, long logId,
        [FromQuery, Range(0, int.MaxValue - 16384)] int offset = 0, CancellationToken cancellationToken = default)
    {
        var rows = await ReadJson("""
            SELECT jsonb_build_object('id', id::text, 'text', substring(logs FROM @offset + 1 FOR 16384),
              'nextOffset', LEAST(length(logs), @offset + 16384), 'hasMore', length(logs) > @offset + 16384)::text
            FROM builds_logs WHERE plugin_slug = @pluginSlug AND build_id = @buildId AND id = @logId
            """, new { pluginSlug, buildId, logId, offset }, cancellationToken);
        return rows.Count == 0 ? NotFound() : Ok(rows[0]);
    }

    [HttpGet("listing-requests")]
    public async Task<IActionResult> Listings([FromQuery] string status = "pending", [FromQuery, Range(0, int.MaxValue)] int after = 0,
        [FromQuery, Range(1, 100)] int limit = 50, CancellationToken cancellationToken = default)
    {
        if (status is not ("all" or "pending" or "approved" or "rejected"))
            return BadRequest(new { message = "Status must be pending, approved, rejected, or all." });
        var rows = await ReadJson($"""
            SELECT ({ListingJson})::text FROM plugin_listing_requests lr WHERE lr.id > @after AND (@status = 'all' OR lr.status = @status)
            ORDER BY lr.id LIMIT @take
            """, new { after, status, take = limit + 1 }, cancellationToken);
        return Page(rows, limit, "id", after.ToString());
    }

    [HttpGet("listing-requests/{requestId:int}")]
    public async Task<IActionResult> Listing(int requestId, CancellationToken cancellationToken)
    {
        var rows = await ReadJson($"SELECT ({ListingJson})::text FROM plugin_listing_requests lr WHERE lr.id = @requestId", new { requestId }, cancellationToken);
        return rows.Count == 0 ? NotFound() : Ok(rows[0]);
    }

    [HttpPost("listing-requests/{requestId:int}/review")]
    public async Task<IActionResult> Review(int requestId, ReviewRequest request, CancellationToken cancellationToken)
    {
        if (request.Decision is not ("approve" or "reject") || string.IsNullOrWhiteSpace(request.Note))
            return BadRequest(new { message = "Specify approve or reject and a nonempty review note." });
        var outcome = await reviews.Review(requestId, User.FindFirstValue(ClaimTypes.NameIdentifier)!, request.Decision == "approve", request.Note,
            slug => Url.Action(nameof(HomeController.GetPluginDetails), "Home", new { pluginSlug = slug }, Request.Scheme), cancellationToken,
            Guid.TryParse(User.FindFirstValue(PluginBuilderAuthenticationSchemes.TokenIdClaim), out var tokenId) ? tokenId : null);
        return outcome switch
        {
            ListingReviewService.Outcome.NotFound => NotFound(),
            ListingReviewService.Outcome.AlreadyProcessed => Conflict(new { message = "This request has already been processed. Fetch its current state." }),
            _ => await Listing(requestId, cancellationToken)
        };
    }

    [HttpGet("audit")]
    public async Task<IActionResult> Audit([FromQuery] Guid? tokenId = null, [FromQuery, Range(1, long.MaxValue)] long? before = null,
        [FromQuery, Range(1, 100)] int limit = 50, CancellationToken cancellationToken = default)
    {
        var rows = await ReadJson("""
            SELECT jsonb_build_object('id', id::text, 'userId', user_id, 'tokenId', token_id, 'method', method, 'path', path,
                'startedAt', started_at, 'completedAt', completed_at, 'statusCode', status_code)::text
            FROM admin_api_audit WHERE (@tokenId IS NULL OR token_id = @tokenId) AND (@before IS NULL OR id < @before)
            ORDER BY id DESC LIMIT @take
            """, new { tokenId, before, take = limit + 1 }, cancellationToken);
        return Page(rows, limit, "id", before?.ToString());
    }

    private async Task<List<JObject>> ReadJson(string sql, object args, CancellationToken cancellationToken)
    {
        await using var conn = await connections.Open(cancellationToken);
        return (await conn.QueryAsync<string>(new CommandDefinition(sql, args, cancellationToken: cancellationToken))).Select(JObject.Parse).ToList();
    }

    private IActionResult Page(List<JObject> rows, int limit, string key, string? cursor) => Ok(new
    {
        items = rows.Take(limit).ToArray(), nextCursor = rows.Count == 0 ? cursor : rows[Math.Min(rows.Count, limit) - 1][key]!.ToString(),
        hasMore = rows.Count > limit
    });

    public sealed class ReviewRequest
    {
        [Required] public string Decision { get; set; } = "";
        [Required, MaxLength(10000)] public string Note { get; set; } = "";
    }
}
