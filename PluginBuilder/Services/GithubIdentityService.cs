using Dapper;
using Newtonsoft.Json.Linq;
using Npgsql;
using PluginBuilder.Util.Extensions;

namespace PluginBuilder.Services;

public class GithubIdentityService(DBConnectionFactory connections, AccountModerationService moderation)
{
    public const string SuspendedMessage = "This account is suspended from creating builds.";
    public const string VerificationRequiredMessage = "Verify your GitHub account before creating a build.";
    private const string BanReason = "Verified GitHub identity is banned by a server admin.";

    // These short transactions serialize proof writes, burn and revocation, including identities with no ban row yet.
    private static Task Serialize(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(734121, 1)", transaction: tx, cancellationToken: ct));

    private static Task<Account?> ReadAccount(NpgsqlConnection conn, string userId, CancellationToken ct) =>
        conn.QuerySingleOrDefaultAsync<Account>(new CommandDefinition("""
            SELECT "GithubUserId" AS GithubUserId, "GithubGistUrl" AS GistUrl,
              CASE WHEN "GithubUserId" IS NOT NULL OR NULLIF("GithubGistUrl", '') IS NOT NULL
                   THEN NULLIF("AccountDetail"->>'github', '') END AS Login,
              EXISTS(SELECT 1 FROM admin_account_locks WHERE user_id = @userId AND locked_until > CURRENT_TIMESTAMP) AS AdminLocked
            FROM "AspNetUsers" WHERE "Id" = @userId
            """, new { userId }, cancellationToken: ct));

    public async Task<string?> GetBuildRestriction(string userId, bool requireVerification, CancellationToken ct = default)
    {
        await using var conn = await connections.Open(ct);
        var account = await ReadAccount(conn, userId, ct);
        if (account is null || account.AdminLocked)
            return SuspendedMessage;
        if (await FindBan(conn, account.GithubUserId, account.Login, ct) is not null)
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            await Serialize(conn, tx, ct);
            var cancelled = new List<AccountModerationService.BuildRow>();
            if (await FindBan(conn, account.GithubUserId, account.Login, ct) is { } ban)
                await LockRelated(conn, tx, userId, ban, BanReason, null, null, cancelled, ct);
            await tx.CommitAsync(ct);
            moderation.Stop(cancelled, AccountModerationService.CancelledByLock);
            return SuspendedMessage;
        }
        return !requireVerification || account.GithubUserId is not null || !string.IsNullOrEmpty(account.GistUrl)
            ? null : VerificationRequiredMessage;
    }

    public async Task<bool> SaveProof(string userId, string gistUrl, VerifiedGithubIdentity identity, CancellationToken ct = default)
    {
        gistUrl = new Uri(gistUrl.Split(['?', '#'], 2)[0]).AbsoluteUri;
        await using var conn = await connections.Open(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await Serialize(conn, tx, ct);
        var account = await ReadAccount(conn, userId, ct);
        if (account is null)
            return false;
        var storedBan = await FindBan(conn, account.GithubUserId, account.Login, ct);
        var ban = storedBan ?? await FindBan(conn, identity.UserId, identity.Login, ct);
        var cancelled = new List<AccountModerationService.BuildRow>();
        // Lock related users before writing a proof: that write can emit an event and lock the event counter.
        if (ban is not null)
            await LockRelated(conn, tx, userId, ban, BanReason, null, null, cancelled, ct);
        // Keep a banned legacy link intact instead of replacing it with another GitHub identity.
        if (account.GithubUserId == identity.UserId || (account.GithubUserId is null && storedBan is null))
            await conn.VerifyGithubAccount(userId, gistUrl, identity);
        else if (ban is null)
            throw new InvalidOperationException("This account is already linked to a different GitHub identity.");

        await tx.CommitAsync(ct);
        moderation.Stop(cancelled, AccountModerationService.CancelledByLock);
        return ban is null;
    }

    public async Task<BurnResult?> Burn(string userId, string reason, string? actor, string? tokenId, CancellationToken ct = default)
    {
        await using var conn = await connections.Open(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await Serialize(conn, tx, ct);
        var account = await ReadAccount(conn, userId, ct);
        if (account is null)
            return null;
        var isAdmin = await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(SELECT 1 FROM "AspNetUserRoles" ur JOIN "AspNetRoles" r ON r."Id" = ur."RoleId"
              WHERE ur."UserId" = @userId AND r."Name" = 'ServerAdmin')
            """, new { userId }, tx, cancellationToken: ct));
        if (isAdmin)
            return new BurnResult(null, null, [], [userId], ["Server admin accounts cannot be burned through the API."]);

        var ban = new Ban(account.GithubUserId, account.Login);
        var created = 0;
        if (ban.GithubUserId is not null || ban.Login is not null)
            created = await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO github_bans (github_user_id, github_login, reason, banned_by)
                VALUES (@GithubUserId, @login, @reason, @actor) ON CONFLICT DO NOTHING
                """, new { ban.GithubUserId, login = ban.Login ?? "", reason, actor }, tx, cancellationToken: ct));
        var cancelled = new List<AccountModerationService.BuildRow>();
        var (locked, skipped) = await LockRelated(conn, tx, userId, ban, reason, actor, tokenId, cancelled, ct);
        if (skipped.Contains(userId))
            return new BurnResult(null, null, [], [userId], ["Server admin accounts cannot be burned through the API."]);
        var warnings = new List<string>();
        if (ban.GithubUserId is null && ban.Login is null)
            warnings.Add("No verified GitHub identity is available. The account was locked, but no GitHub ban was created.");
        if (skipped.Length > 0)
            warnings.Add("Related server admin accounts were not locked.");
        if (created > 0)
            await moderation.Emit(conn, tx, "github.banned", new JObject
            {
                ["githubUserId"] = ban.GithubUserId, ["githubLogin"] = ban.Login, ["reason"] = reason
            }, actor, tokenId, ct);
        await moderation.Emit(conn, tx, "user.burned", new JObject
        {
            ["userId"] = userId, ["githubUserId"] = ban.GithubUserId, ["githubLogin"] = ban.Login, ["reason"] = reason,
            ["lockedUserIds"] = new JArray(locked), ["skippedAdminUserIds"] = new JArray(skipped), ["warnings"] = new JArray(warnings)
        }, actor, tokenId, ct);
        await tx.CommitAsync(ct);
        moderation.Stop(cancelled, AccountModerationService.CancelledByLock);
        return new BurnResult(ban.GithubUserId, ban.Login, locked, skipped, warnings.ToArray());
    }

    public async Task<bool> Revoke(long banId, string reason, string? actor, string? tokenId, CancellationToken ct = default)
    {
        await using var conn = await connections.Open(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await Serialize(conn, tx, ct);
        var ban = await conn.QuerySingleOrDefaultAsync<Ban>(new CommandDefinition("""
            DELETE FROM github_bans WHERE id = @banId RETURNING github_user_id AS GithubUserId, github_login AS Login
            """, new { banId }, tx, cancellationToken: ct));
        if (ban is null)
            return false;
        await moderation.Emit(conn, tx, "github.ban_revoked", new JObject
        {
            ["banId"] = banId, ["githubUserId"] = ban.GithubUserId, ["githubLogin"] = ban.Login, ["reason"] = reason
        }, actor, tokenId, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    private static Task<Ban?> FindBan(NpgsqlConnection conn, long? id, string? login, CancellationToken ct) =>
        conn.QueryFirstOrDefaultAsync<Ban>(new CommandDefinition("""
            SELECT github_user_id AS GithubUserId, github_login AS Login FROM github_bans
            WHERE github_user_id = @id OR (github_user_id IS NULL AND lower(github_login) = lower(@login))
            ORDER BY github_user_id NULLS LAST LIMIT 1
            """, new { id, login }, cancellationToken: ct));

    private async Task<(string[] Locked, string[] Skipped)> LockRelated(NpgsqlConnection conn, NpgsqlTransaction tx,
        string userId, Ban ban, string reason, string? actor, string? tokenId,
        List<AccountModerationService.BuildRow> cancelled, CancellationToken ct)
    {
        var users = (await conn.QueryAsync<RelatedAccount>(new CommandDefinition("""
            SELECT u."Id" AS Id,
              EXISTS(SELECT 1 FROM "AspNetUserRoles" ur JOIN "AspNetRoles" r ON r."Id" = ur."RoleId"
                WHERE ur."UserId" = u."Id" AND r."Name" = 'ServerAdmin') AS IsAdmin
            FROM "AspNetUsers" u WHERE u."Id" = @userId OR u."GithubUserId" = @GithubUserId
              OR (@GithubUserId IS NULL AND lower(u."AccountDetail"->>'github') = lower(@login)
                  AND (u."GithubUserId" IS NOT NULL OR NULLIF(u."GithubGistUrl", '') IS NOT NULL))
            ORDER BY u."Id" FOR UPDATE OF u
            """, new { userId, ban.GithubUserId, login = ban.Login }, tx, cancellationToken: ct))).ToArray();
        // Lock all affected builds before the first event locks the event counter. Publishers lock builds before emitting too.
        await conn.ExecuteAsync(new CommandDefinition("""
            SELECT 1 FROM builds WHERE triggered_by = ANY(@userIds) AND state <> ALL(@terminal)
            ORDER BY plugin_slug, id FOR UPDATE
            """, new { userIds = users.Where(u => !u.IsAdmin).Select(u => u.Id).ToArray(),
                terminal = BuildStatesExtensions.TerminalEventNames }, tx, cancellationToken: ct));
        foreach (var user in users.Where(u => !u.IsAdmin))
        {
            // Automatic reapplication keeps the metadata of an existing indefinite lock.
            if (actor is null && await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                    "SELECT EXISTS(SELECT 1 FROM admin_account_locks WHERE user_id = @id AND locked_until = 'infinity')",
                    new { id = user.Id }, tx, cancellationToken: ct)))
                continue;
            cancelled.AddRange(await moderation.LockAccount(conn, tx, user.Id, null, reason, actor, tokenId, ct));
        }
        if (actor is null)
            await moderation.Emit(conn, tx, "github.ban_applied", new JObject
            {
                ["userId"] = userId, ["githubUserId"] = ban.GithubUserId, ["githubLogin"] = ban.Login,
                ["lockedUserIds"] = new JArray(users.Where(u => !u.IsAdmin).Select(u => u.Id)),
                ["skippedAdminUserIds"] = new JArray(users.Where(u => u.IsAdmin).Select(u => u.Id))
            }, null, null, ct);
        return (users.Where(u => !u.IsAdmin).Select(u => u.Id).ToArray(), users.Where(u => u.IsAdmin).Select(u => u.Id).ToArray());
    }

    public sealed record BurnResult(long? GithubUserId, string? GithubLogin, string[] LockedUserIds, string[] SkippedAdminUserIds, string[] Warnings);
    private sealed record Ban(long? GithubUserId, string? Login);
    private sealed class Account
    {
        public long? GithubUserId { get; set; }
        public string? GistUrl { get; set; }
        public string? Login { get; set; }
        public bool AdminLocked { get; set; }
    }
    private sealed class RelatedAccount
    {
        public string Id { get; set; } = "";
        public bool IsAdmin { get; set; }
    }
}
