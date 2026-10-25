using Dapper;
using Newtonsoft.Json.Linq;
using Npgsql;
using PluginBuilder.Util.Extensions;

namespace PluginBuilder.Services;

public class GithubIdentityService(DBConnectionFactory connections, ExternalAccountVerificationService proofs,
    AccountModerationService moderation)
{
    private const string BanReason = "Verified GitHub identity is banned by a server admin.";

    // These short transactions serialize proof writes, burn and revocation, including identities with no ban row yet.
    private static Task Serialize(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(734121, 1)", transaction: tx, cancellationToken: ct));

    private static Task<Account?> ReadAccount(NpgsqlConnection conn, string userId, CancellationToken ct) =>
        conn.QuerySingleOrDefaultAsync<Account>(new CommandDefinition("""
            SELECT "Id" AS Id, "GithubUserId" AS GithubUserId, "GithubGistUrl" AS GistUrl,
              "AccountDetail"->>'github' AS Login,
              EXISTS(SELECT 1 FROM admin_account_locks WHERE user_id = @userId AND locked_until > CURRENT_TIMESTAMP) AS AdminLocked
            FROM "AspNetUsers" WHERE "Id" = @userId
            """, new { userId }, cancellationToken: ct));

    public async Task<long?> Recover(string userId, CancellationToken ct = default)
    {
        Account? account;
        await using (var conn = await connections.Open(ct))
            account = await ReadAccount(conn, userId, ct);
        if (account is null || account.GithubUserId is not null || string.IsNullOrEmpty(account.GistUrl))
            return account?.GithubUserId;

        // Never hold a database connection while waiting for GitHub. A missing/deleted gist leaves the old proof intact.
        var identity = await proofs.RecoverGistIdentity(account.GistUrl, userId, ct);
        if (identity is null)
            return null;

        await using var connection = await connections.Open(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await Serialize(connection, tx, ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE "AspNetUsers" SET "GithubUserId" = @id,
              "AccountDetail" = jsonb_set(COALESCE("AccountDetail", '{}'::jsonb), '{github}', to_jsonb(@login::text))
            WHERE "Id" = @userId AND "GithubUserId" IS NULL AND "GithubGistUrl" = @expectedProof
            """, new { userId, id = identity.UserId, login = identity.Login, expectedProof = account.GistUrl }, tx, cancellationToken: ct));
        var current = await ReadAccount(connection, userId, ct);
        await tx.CommitAsync(ct);
        return current?.GithubUserId;
    }

    public async Task<bool> CanBuild(string userId, bool requireVerification, CancellationToken ct = default)
    {
        Account? account;
        await using (var connection = await connections.Open(ct))
            account = await ReadAccount(connection, userId, ct);
        if (account is null || account.AdminLocked)
            return false;
        if (account.GithubUserId is null && !string.IsNullOrEmpty(account.GistUrl))
            await Recover(userId, ct);
        await using var conn = await connections.Open(ct);
        account = await ReadAccount(conn, userId, ct);
        if (account is null || account.AdminLocked)
            return false;
        if (account.GithubUserId is { } id && await IsBanned(conn, id, ct))
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            await Serialize(conn, tx, ct);
            var cancelled = new List<AccountModerationService.BuildRow>();
            if (await IsBanned(conn, id, ct))
                await LockRelated(conn, tx, userId, id, BanReason, null, null, cancelled, ct);
            await tx.CommitAsync(ct);
            moderation.Stop(cancelled, AccountModerationService.CancelledByLock);
            return false;
        }
        return account.GithubUserId is not null || (!requireVerification && string.IsNullOrEmpty(account.GistUrl));
    }

    public async Task<bool> SaveProof(string userId, string gistUrl, VerifiedGithubIdentity identity, CancellationToken ct = default)
    {
        // The proof was already validated; optional URL suffixes are not part of the stored gist identity.
        gistUrl = new Uri(gistUrl.Split(['?', '#'], 2)[0]).AbsoluteUri;
        await Recover(userId, ct);
        await using var conn = await connections.Open(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await Serialize(conn, tx, ct);
        var account = await ReadAccount(conn, userId, ct);
        if (account is null)
            return false;
        var githubUserId = identity.UserId;
        if (account.GithubUserId is { } previousId && previousId != githubUserId && await IsBanned(conn, previousId, ct))
            githubUserId = previousId;
        var banned = await IsBanned(conn, githubUserId, ct);
        var cancelled = new List<AccountModerationService.BuildRow>();
        // Lock related users before writing a proof: that write can emit an event and lock the event counter.
        if (banned)
            await LockRelated(conn, tx, userId, githubUserId, BanReason, null, null, cancelled, ct);
        if (account.GithubUserId is null || account.GithubUserId == identity.UserId)
            await conn.VerifyGithubAccount(userId, gistUrl, identity);
        else if (!banned)
            throw new InvalidOperationException("This account is already linked to a different GitHub identity.");

        await tx.CommitAsync(ct);
        moderation.Stop(cancelled, AccountModerationService.CancelledByLock);
        return !banned;
    }

    public async Task<BurnResult?> Burn(string userId, string reason, string? actor, string? tokenId, CancellationToken ct = default)
    {
        await Recover(userId, ct);
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
            return new BurnResult(null, [], [userId], ["Server admin accounts cannot be burned through the API."]);

        var created = 0;
        if (account.GithubUserId is { } id)
            created = await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO github_bans (github_user_id, github_login, reason, banned_by)
                VALUES (@id, @login, @reason, @actor)
                ON CONFLICT (github_user_id) DO NOTHING
                """, new { id, login = account.Login ?? "", reason, actor }, tx, cancellationToken: ct));
        var cancelled = new List<AccountModerationService.BuildRow>();
        var (locked, skipped) = await LockRelated(conn, tx, userId, account.GithubUserId, reason, actor, tokenId, cancelled, ct);
        if (skipped.Contains(userId))
            return new BurnResult(null, [], [userId], ["Server admin accounts cannot be burned through the API."]);
        var warnings = new List<string>();
        if (account.GithubUserId is null)
            warnings.Add("GitHub identity could not be recovered. The account was locked, but no GitHub ban was created.");
        if (skipped.Length > 0)
            warnings.Add("Related server admin accounts were not locked.");
        if (created > 0)
            await moderation.Emit(conn, tx, "github.banned", new JObject
            {
                ["githubUserId"] = account.GithubUserId, ["githubLogin"] = account.Login, ["reason"] = reason
            }, actor, tokenId, ct);
        await moderation.Emit(conn, tx, "user.burned", new JObject
        {
            ["userId"] = userId, ["githubUserId"] = account.GithubUserId, ["reason"] = reason,
            ["lockedUserIds"] = new JArray(locked), ["skippedAdminUserIds"] = new JArray(skipped), ["warnings"] = new JArray(warnings)
        }, actor, tokenId, ct);
        await tx.CommitAsync(ct);
        moderation.Stop(cancelled, AccountModerationService.CancelledByLock);
        return new BurnResult(account.GithubUserId, locked, skipped, warnings.ToArray());
    }

    public async Task<bool> Revoke(long githubUserId, string reason, string? actor, string? tokenId, CancellationToken ct = default)
    {
        await using var conn = await connections.Open(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await Serialize(conn, tx, ct);
        var removed = await conn.ExecuteAsync(new CommandDefinition("DELETE FROM github_bans WHERE github_user_id = @githubUserId",
            new { githubUserId }, tx, cancellationToken: ct));
        if (removed == 0)
            return false;
        await moderation.Emit(conn, tx, "github.ban_revoked", new JObject { ["githubUserId"] = githubUserId, ["reason"] = reason }, actor, tokenId, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    private static Task<bool> IsBanned(NpgsqlConnection conn, long id, CancellationToken ct) =>
        conn.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM github_bans WHERE github_user_id = @id)",
            new { id }, cancellationToken: ct));

    private async Task<(string[] Locked, string[] Skipped)> LockRelated(NpgsqlConnection conn, NpgsqlTransaction tx,
        string userId, long? githubUserId, string reason, string? actor, string? tokenId,
        List<AccountModerationService.BuildRow> cancelled, CancellationToken ct)
    {
        var users = (await conn.QueryAsync<RelatedAccount>(new CommandDefinition("""
            SELECT u."Id" AS Id,
              EXISTS(SELECT 1 FROM "AspNetUserRoles" ur JOIN "AspNetRoles" r ON r."Id" = ur."RoleId"
                WHERE ur."UserId" = u."Id" AND r."Name" = 'ServerAdmin') AS IsAdmin
            FROM "AspNetUsers" u WHERE u."Id" = @userId OR u."GithubUserId" = @githubUserId
            ORDER BY u."Id" FOR UPDATE OF u
            """, new { userId, githubUserId }, tx, cancellationToken: ct))).ToArray();
        // Lock all affected builds before the first event locks the event counter. Publishers lock builds before emitting too.
        await conn.ExecuteAsync(new CommandDefinition("""
            SELECT 1 FROM builds WHERE triggered_by = ANY(@userIds) AND state <> ALL(@terminal)
            ORDER BY plugin_slug, id FOR UPDATE
            """, new { userIds = users.Where(u => !u.IsAdmin).Select(u => u.Id).ToArray(),
                terminal = BuildStatesExtensions.TerminalEventNames }, tx, cancellationToken: ct));
        foreach (var user in users.Where(u => !u.IsAdmin))
            cancelled.AddRange(await moderation.LockAccount(conn, tx, user.Id, null, reason, actor, tokenId, ct));
        if (actor is null && githubUserId is not null)
            await moderation.Emit(conn, tx, "github.ban_applied", new JObject
            {
                ["userId"] = userId, ["githubUserId"] = githubUserId,
                ["lockedUserIds"] = new JArray(users.Where(u => !u.IsAdmin).Select(u => u.Id)),
                ["skippedAdminUserIds"] = new JArray(users.Where(u => u.IsAdmin).Select(u => u.Id))
            }, null, null, ct);
        return (users.Where(u => !u.IsAdmin).Select(u => u.Id).ToArray(), users.Where(u => u.IsAdmin).Select(u => u.Id).ToArray());
    }

    public sealed record BurnResult(long? GithubUserId, string[] LockedUserIds, string[] SkippedAdminUserIds, string[] Warnings);
    private sealed class Account
    {
        public string Id { get; set; } = "";
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
