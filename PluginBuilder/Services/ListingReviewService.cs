using Dapper;
using Microsoft.AspNetCore.OutputCaching;
using PluginBuilder.DataModels;

namespace PluginBuilder.Services;

public class ListingReviewService(DBConnectionFactory connections, EmailService emails, IOutputCacheStore cache)
{
    public enum Outcome { Completed, NotFound, AlreadyProcessed }

    public async Task<Outcome> Review(int requestId, string reviewerId, bool approve, string? note,
        Func<string, string?> publicUrl, CancellationToken cancellationToken = default, Guid? tokenId = null)
    {
        if (!approve && string.IsNullOrWhiteSpace(note))
            throw new ArgumentException("A rejection reason is required.", nameof(note));
        if (note?.Length > 10000)
            throw new ArgumentException("Review note must not exceed 10000 characters.", nameof(note));
        await using var conn = await connections.Open(cancellationToken);
        await using var transaction = await conn.BeginTransactionAsync(cancellationToken);
        var row = await conn.QuerySingleOrDefaultAsync<ReviewRow>(new CommandDefinition("""
            SELECT lr.plugin_slug AS PluginSlug, lr.status, p.settings->>'pluginTitle' AS Title,
                (SELECT u."Email" FROM users_plugins up JOIN "AspNetUsers" u ON u."Id" = up.user_id
                 WHERE up.plugin_slug = p.slug AND up.is_primary_owner LIMIT 1) AS Email
            FROM plugin_listing_requests lr JOIN plugins p ON p.slug = lr.plugin_slug
            WHERE lr.id = @requestId FOR UPDATE OF lr, p
            """, new { requestId }, transaction, cancellationToken: cancellationToken));
        if (row is null)
            return Outcome.NotFound;
        if (row.Status != "pending")
            return Outcome.AlreadyProcessed;
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE plugin_listing_requests SET status = @status, reviewed_at = CURRENT_TIMESTAMP,
                reviewed_by = @reviewerId, reviewed_by_token = @tokenId, review_note = @note, rejection_reason = @reason
            WHERE id = @requestId;
            """, new { requestId, reviewerId, tokenId, status = approve ? "approved" : "rejected", note = note?.Trim(), reason = approve ? null : note?.Trim() },
            transaction, cancellationToken: cancellationToken));
        if (approve)
            await conn.ExecuteAsync(new CommandDefinition("UPDATE plugins SET visibility = 'listed' WHERE slug = @PluginSlug",
                new { row.PluginSlug }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        // Perform external effects only after the decision and visibility commit.
        if (approve)
            await cache.EvictByTagAsync(CacheTags.Plugins, CancellationToken.None);
        var explanation = approve ? publicUrl(row.PluginSlug) : note?.Trim();
        if (!string.IsNullOrEmpty(row.Email) && !string.IsNullOrEmpty(explanation))
            await emails.NotifyPluginOwnerForRequestListingStatus(row.Email, row.Title ?? row.PluginSlug, approve, explanation);
        return Outcome.Completed;
    }

    private sealed class ReviewRow
    {
        public string PluginSlug { get; set; } = "";
        public string Status { get; set; } = "";
        public string? Title { get; set; }
        public string? Email { get; set; }
    }
}
