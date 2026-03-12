using System.Text;
using Dapper;
using Microsoft.AspNetCore.OutputCaching;
using PluginBuilder.DataModels;
using PluginBuilder.Events;
using PluginBuilder.JsonConverters;
using PluginBuilder.Util;
using PluginBuilder.Util.Extensions;

namespace PluginBuilder.Services;

public enum VersionLifecycleFailureCode
{
    NotFound,
    InvalidBuildState,
    SignatureRequired,
    ManifestUnavailable,
    SignatureVerificationFailed
}

public readonly record struct VersionLifecycleResult(
    bool Success,
    VersionLifecycleFailureCode? FailureCode = null,
    string? Message = null,
    long? BuildId = null)
{
    public static VersionLifecycleResult Ok(long? buildId = null)
    {
        return new VersionLifecycleResult(true, BuildId: buildId);
    }

    public static VersionLifecycleResult Fail(VersionLifecycleFailureCode failureCode, string message)
    {
        return new VersionLifecycleResult(false, failureCode, message);
    }
}

public sealed class VersionLifecycleService(
    DBConnectionFactory connectionFactory,
    GPGKeyService gpgKeyService,
    EventAggregator eventAggregator,
    IOutputCacheStore outputCacheStore)
{
    public async Task<VersionLifecycleResult> ReleaseAsync(
        PluginSlug pluginSlug,
        PluginVersion version,
        string userId,
        byte[]? signatureBytes)
    {
        await using var conn = await connectionFactory.Open();
        var row = await conn.QueryFirstOrDefaultAsync<(long build_id, string state, string? manifest_info, string? settings)?>(
            """
            SELECT v.build_id, b.state, b.manifest_info::text, p.settings::text
            FROM versions v
            JOIN builds b ON v.plugin_slug = b.plugin_slug AND v.build_id = b.id
            JOIN plugins p ON v.plugin_slug = p.slug
            WHERE v.plugin_slug = @pluginSlug AND v.ver = @version
            LIMIT 1
            """,
            new { pluginSlug = pluginSlug.ToString(), version = version.VersionParts });

        if (row is null)
            return VersionLifecycleResult.Fail(VersionLifecycleFailureCode.NotFound, "Version not found");

        if (row.Value.state != BuildStates.Uploaded.ToEventName())
        {
            return VersionLifecycleResult.Fail(
                VersionLifecycleFailureCode.InvalidBuildState,
                $"Build is in '{row.Value.state}' state and cannot be released");
        }

        if (signatureBytes is { Length: > 0 })
        {
            var niceManifest = ManifestHelper.NiceJson(row.Value.manifest_info);
            var manifestHash = ManifestHelper.GetManifestHash(niceManifest, true);
            if (string.IsNullOrEmpty(manifestHash))
            {
                return VersionLifecycleResult.Fail(
                    VersionLifecycleFailureCode.ManifestUnavailable,
                    "Manifest information for plugin not available");
            }

            var signatureVerification = await gpgKeyService.VerifyDetachedSignature(
                pluginSlug.ToString(),
                userId,
                Encoding.UTF8.GetBytes(manifestHash),
                signatureBytes);

            if (!signatureVerification.valid)
            {
                return VersionLifecycleResult.Fail(
                    VersionLifecycleFailureCode.SignatureVerificationFailed,
                    signatureVerification.message);
            }

            var updated = await conn.UpdateVersionReleaseStatus(pluginSlug, "sign_release", version, signatureVerification.proof);
            if (!updated)
                return VersionLifecycleResult.Fail(VersionLifecycleFailureCode.NotFound, "Version not found");
        }
        else
        {
            var pluginSettings = SafeJson.Deserialize<PluginSettings>(row.Value.settings);
            if (pluginSettings?.RequireGPGSignatureForRelease == true)
            {
                return VersionLifecycleResult.Fail(
                    VersionLifecycleFailureCode.SignatureRequired,
                    "A verified GPG signature is required to release this version");
            }

            var updated = await conn.UpdateVersionReleaseStatus(pluginSlug, "release", version);
            if (!updated)
                return VersionLifecycleResult.Fail(VersionLifecycleFailureCode.NotFound, "Version not found");
        }

        await outputCacheStore.EvictByTagAsync(CacheTags.Plugins, CancellationToken.None);
        return VersionLifecycleResult.Ok();
    }

    public async Task<VersionLifecycleResult> UnreleaseAsync(PluginSlug pluginSlug, PluginVersion version)
    {
        await using var conn = await connectionFactory.Open();
        var updated = await conn.UpdateVersionReleaseStatus(pluginSlug, "unrelease", version);
        if (!updated)
            return VersionLifecycleResult.Fail(VersionLifecycleFailureCode.NotFound, "Version not found");

        await outputCacheStore.EvictByTagAsync(CacheTags.Plugins, CancellationToken.None);
        return VersionLifecycleResult.Ok();
    }

    public async Task<VersionLifecycleResult> RemoveAsync(PluginSlug pluginSlug, PluginVersion version)
    {
        await using var conn = await connectionFactory.Open();
        var buildId = await conn.QueryFirstOrDefaultAsync<long?>(
            "SELECT build_id FROM versions WHERE plugin_slug=@pluginSlug AND ver=@version",
            new { pluginSlug = pluginSlug.ToString(), version = version.VersionParts });

        if (buildId is null)
            return VersionLifecycleResult.Fail(VersionLifecycleFailureCode.NotFound, "Version not found");

        await using var tx = await conn.BeginTransactionAsync();
        var fullBuildId = new FullBuildId(pluginSlug, buildId.Value);
        await conn.UpdateBuild(fullBuildId, BuildStates.Removed, null, tx: tx);
        await conn.ExecuteAsync(
            "DELETE FROM versions WHERE plugin_slug=@pluginSlug AND ver=@version",
            new { pluginSlug = pluginSlug.ToString(), version = version.VersionParts },
            tx);
        await tx.CommitAsync();

        eventAggregator.Publish(new BuildChanged(fullBuildId, BuildStates.Removed));
        await outputCacheStore.EvictByTagAsync(CacheTags.Plugins, CancellationToken.None);

        return VersionLifecycleResult.Ok(buildId.Value);
    }
}
