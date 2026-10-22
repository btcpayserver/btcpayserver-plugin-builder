using Dapper;
using PluginBuilder.Util.Extensions;

namespace PluginBuilder.Services;

public class GithubIdentityService(DBConnectionFactory connectionFactory, ExternalAccountVerificationService verificationService)
{
    public async Task<long?> RecoverUserId(string userId, CancellationToken cancellationToken = default)
    {
        StoredProof? proof;
        await using (var conn = await connectionFactory.Open(cancellationToken))
        {
            proof = await conn.QuerySingleOrDefaultAsync<StoredProof>(
                "SELECT \"GithubUserId\", \"GithubGistUrl\" FROM \"AspNetUsers\" WHERE \"Id\" = @userId", new { userId });
        }
        if (proof?.GithubUserId is not null)
            return proof.GithubUserId;
        if (string.IsNullOrEmpty(proof?.GithubGistUrl))
            return null;

        var identity = await verificationService.RecoverGistIdentity(proof.GithubGistUrl, userId, cancellationToken);
        await using var updateConnection = await connectionFactory.Open(cancellationToken);
        if (identity is not null)
            await updateConnection.VerifyGithubAccount(userId, proof.GithubGistUrl, identity, expectedLegacyProof: proof.GithubGistUrl);

        // A new verification may have replaced the stored proof while GitHub was responding.
        return await updateConnection.GetGithubUserId(userId);
    }

    private sealed class StoredProof
    {
        public long? GithubUserId { get; set; }
        public string? GithubGistUrl { get; set; }
    }
}
