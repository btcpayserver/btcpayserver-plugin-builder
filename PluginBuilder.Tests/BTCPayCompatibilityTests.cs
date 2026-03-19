using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.OutputCaching;
using Newtonsoft.Json.Linq;
using PluginBuilder.DataModels;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests;

public class BTCPayCompatibilityTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    private sealed class MinOverrideRow
    {
        public int[] EffectiveMin { get; init; } = [];
        public bool OverrideEnabled { get; init; }
    }

    private sealed class MaxOverrideRow
    {
        public int[]? EffectiveMax { get; init; }
        public bool OverrideEnabled { get; init; }
    }

    [Fact]
    public void ParseBTCPayCondition_DerivesMinAndMax()
    {
        var manifest = PluginManifest.Parse(CreateManifest(">= 1.2.3 && <= 2.0.0"), strictBTCPayVersionCondition: true);

        Assert.Equal("1.2.3", manifest.BTCPayMinVersion.Version);
        Assert.Equal("2.0.0", manifest.BTCPayMaxVersion.Version);
    }

    [Fact]
    public void ParseBTCPayCondition_DerivesMinWithoutMax_AndAllowsExtraSpaces()
    {
        var manifest = PluginManifest.Parse(CreateManifest("  >=   1.2.3.4   "), strictBTCPayVersionCondition: true);

        Assert.Equal("1.2.3.4", manifest.BTCPayMinVersion.Version);
        Assert.Null(manifest.BTCPayMaxVersion);
    }

    [Theory]
    [InlineData("< 2.0.0")]
    [InlineData(">= 1.2.3 && < 2.0.0")]
    [InlineData(">= 1.2.3 || <= 2.0.0")]
    [InlineData(">= 1.2.3 && > 2.0.0")]
    public void ParseBTCPayCondition_RejectsUnsupportedSyntax(string condition)
    {
        Assert.Throws<FormatException>(() => PluginManifest.Parse(CreateManifest(condition), strictBTCPayVersionCondition: true));
    }

    [Fact]
    public void ParseBTCPayCondition_RejectsDecreasingRange()
    {
        Assert.Throws<FormatException>(() =>
            PluginManifest.Parse(CreateManifest(">= 2.0.0 && <= 1.9.9"), strictBTCPayVersionCondition: true));
    }

    [Fact]
    public async Task PublicApi_FiltersByMaxVersion()
    {
        await using var tester = Create();
        tester.ReuseDatabase = false;
        await tester.Start();

        var ownerId = await tester.CreateFakeUserAsync();
        var pluginSlug = "btcpay-max-" + Guid.NewGuid().ToString("N")[..8];
        var fullBuildId = await tester.CreateAndBuildPluginAsync(ownerId, pluginSlug);

        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        var buildRow = await conn.QuerySingleAsync<(string manifest_info, string build_info)>(
            "SELECT manifest_info, build_info FROM builds WHERE plugin_slug = @pluginSlug AND id = @buildId",
            new { pluginSlug, buildId = fullBuildId.BuildId });
        var manifest = PluginManifest.Parse(buildRow.manifest_info);
        await conn.SetVersionBuild(fullBuildId, manifest.Version, manifest.BTCPayMinVersion, PluginVersion.Parse("2.0.0.0"), false);
        await conn.SetPluginSettings(pluginSlug, new PluginSettings
        {
            PluginTitle = pluginSlug,
            Description = "Compatibility test plugin",
            GitRepository = ServerTester.RepoUrl
        }, PluginVisibilityEnum.Listed);

        var outputCacheStore = tester.GetService<IOutputCacheStore>();
        await outputCacheStore.EvictByTagAsync(CacheTags.Plugins, CancellationToken.None);

        var client = tester.CreateHttpClient();
        var compatible = await client.GetPublishedVersions("1.9.9.9", false);
        var maxBoundary = await client.GetPublishedVersions("2.0.0.0", false);
        var incompatible = await client.GetPublishedVersions("2.0.0.1", false);

        var version = Assert.Single(compatible);
        Assert.Equal(pluginSlug, version.ProjectSlug);
        Assert.Equal(manifest.BTCPayMinVersion?.ToString(), version.BTCPayMinVersion);
        Assert.Equal("2.0.0.0", version.BTCPayMaxVersion);
        Assert.Single(maxBoundary);
        Assert.Empty(incompatible);
    }

    [Fact]
    public async Task PublicApi_FiltersByShortMaxVersionUsingZeroPaddedComparison()
    {
        await using var tester = Create();
        tester.ReuseDatabase = false;
        await tester.Start();

        var ownerId = await tester.CreateFakeUserAsync();
        var pluginSlug = "btcpay-short-max-" + Guid.NewGuid().ToString("N")[..8];
        var fullBuildId = await tester.CreateAndBuildPluginAsync(ownerId, pluginSlug);

        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        var buildRow = await conn.QuerySingleAsync<(string manifest_info, string build_info)>(
            "SELECT manifest_info, build_info FROM builds WHERE plugin_slug = @pluginSlug AND id = @buildId",
            new { pluginSlug, buildId = fullBuildId.BuildId });
        var manifest = PluginManifest.Parse(buildRow.manifest_info);
        await conn.SetVersionBuild(fullBuildId, manifest.Version, manifest.BTCPayMinVersion, PluginVersion.Parse("2.1"), false);
        await conn.SetPluginSettings(pluginSlug, new PluginSettings
        {
            PluginTitle = pluginSlug,
            Description = "Short max compatibility test plugin",
            GitRepository = ServerTester.RepoUrl
        }, PluginVisibilityEnum.Listed);

        var outputCacheStore = tester.GetService<IOutputCacheStore>();
        await outputCacheStore.EvictByTagAsync(CacheTags.Plugins, CancellationToken.None);

        var client = tester.CreateHttpClient();
        var compatible = await client.GetPublishedVersions("2.1.0.0", false);
        var incompatible = await client.GetPublishedVersions("2.1.0.1", false);

        var version = Assert.Single(compatible);
        Assert.Equal(pluginSlug, version.ProjectSlug);
        Assert.Equal("2.1", version.BTCPayMaxVersion);
        Assert.Empty(incompatible);
    }

    [Fact]
    public async Task PublicApi_SelectsLatestCompatibleVersionAcrossBTCPayRanges()
    {
        await using var tester = Create();
        tester.ReuseDatabase = false;
        await tester.Start();

        var ownerId = await tester.CreateFakeUserAsync();
        var pluginSlug = "btcpay-range-" + Guid.NewGuid().ToString("N")[..8];
        var fullBuildId = await tester.CreateAndBuildPluginAsync(ownerId, pluginSlug);

        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        var buildRow = await conn.QuerySingleAsync<(string manifest_info, string build_info)>(
            "SELECT manifest_info, build_info FROM builds WHERE plugin_slug = @pluginSlug AND id = @buildId",
            new { pluginSlug, buildId = fullBuildId.BuildId });
        var manifest = PluginManifest.Parse(buildRow.manifest_info);

        await conn.SetVersionBuild(fullBuildId, manifest.Version, manifest.BTCPayMinVersion, PluginVersion.Parse("2.0.0.0"), false);
        await conn.SetPluginSettings(pluginSlug, new PluginSettings
        {
            PluginTitle = pluginSlug,
            Description = "Compatibility split test plugin",
            GitRepository = ServerTester.RepoUrl
        }, PluginVisibilityEnum.Listed);

        await conn.ExecuteAsync(
            """
            INSERT INTO versions (plugin_slug, ver, build_id, btcpay_min_ver, btcpay_max_ver, pre_release)
            VALUES (@pluginSlug, @version, @buildId, @btcpayMinVer, NULL, FALSE)
            """,
            new
            {
                pluginSlug,
                version = PluginVersion.Parse("1.0.3.0").VersionParts,
                buildId = fullBuildId.BuildId,
                btcpayMinVer = PluginVersion.Parse("2.0.0.0").VersionParts
            });

        var outputCacheStore = tester.GetService<IOutputCacheStore>();
        await outputCacheStore.EvictByTagAsync(CacheTags.Plugins, CancellationToken.None);

        var client = tester.CreateHttpClient();
        var legacy = await client.GetPublishedVersions("1.9.9.9", false);
        var modern = await client.GetPublishedVersions("2.1.0.0", false);

        Assert.Equal(manifest.Version.ToString(), Assert.Single(legacy).Version);
        Assert.Equal("1.0.3.0", Assert.Single(modern).Version);
    }

    [Fact]
    public async Task MinMaxOverridesPersistAcrossRebuild()
    {
        await using var tester = Create();
        tester.ReuseDatabase = false;
        await tester.Start();

        var ownerId = await tester.CreateFakeUserAsync();
        var pluginSlug = "btcpay-override-" + Guid.NewGuid().ToString("N")[..8];
        var fullBuildId = await tester.CreateAndBuildPluginAsync(ownerId, pluginSlug);

        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        var buildRow = await conn.QuerySingleAsync<(string manifest_info, string build_info)>(
            "SELECT manifest_info, build_info FROM builds WHERE plugin_slug = @pluginSlug AND id = @buildId",
            new { pluginSlug, buildId = fullBuildId.BuildId });
        var manifest = PluginManifest.Parse(buildRow.manifest_info);

        Assert.Equal(1, await conn.ExecuteAsync(
            """
            UPDATE versions
            SET btcpay_min_ver = @overrideMin,
                btcpay_min_ver_override_enabled = TRUE
            WHERE plugin_slug = @pluginSlug AND ver = @version
            """,
            new
            {
                pluginSlug,
                version = manifest.Version.VersionParts,
                overrideMin = PluginVersion.Parse("2.0.0.0").VersionParts
            }));
        Assert.Equal(1, await conn.ExecuteAsync(
            """
            UPDATE versions
            SET btcpay_max_ver = @overrideMax,
                btcpay_max_ver_override_enabled = TRUE
            WHERE plugin_slug = @pluginSlug AND ver = @version
            """,
            new
            {
                pluginSlug,
                version = manifest.Version.VersionParts,
                overrideMax = PluginVersion.Parse("2.5.0.0").VersionParts
            }));

        var rebuildManifestJson = JObject.Parse(buildRow.manifest_info);
        ((JObject)rebuildManifestJson["Dependencies"]![0]!)["Condition"] = ">= 1.5.0 && <= 3.0.0";
        var rebuildManifest = PluginManifest.Parse(rebuildManifestJson.ToString());
        var rebuiltBuildId = await conn.NewBuild(new PluginSlug(pluginSlug), new PluginBuildParameters(ServerTester.RepoUrl)
        {
            GitRef = ServerTester.GitRef,
            PluginDirectory = ServerTester.PluginDir,
            BuildConfig = ServerTester.BuildCfg
        });
        var rebuiltFullBuildId = new FullBuildId(new PluginSlug(pluginSlug), rebuiltBuildId);
        await conn.UpdateBuild(rebuiltFullBuildId, BuildStates.Uploaded, JObject.Parse(buildRow.build_info), rebuildManifest);

        Assert.True(await conn.SetVersionBuild(rebuiltFullBuildId, rebuildManifest.Version, rebuildManifest.BTCPayMinVersion, rebuildManifest.BTCPayMaxVersion, true));

        var minRow = await conn.QuerySingleAsync<MinOverrideRow>(
            """
            SELECT btcpay_min_ver AS "EffectiveMin",
                   btcpay_min_ver_override_enabled AS "OverrideEnabled"
            FROM versions
            WHERE plugin_slug = @pluginSlug AND ver = @version
            LIMIT 1
            """,
            new
            {
                pluginSlug,
                version = manifest.Version.VersionParts
            });
        var maxRow = await conn.QuerySingleAsync<MaxOverrideRow>(
            """
            SELECT btcpay_max_ver AS "EffectiveMax",
                   btcpay_max_ver_override_enabled AS "OverrideEnabled"
            FROM versions
            WHERE plugin_slug = @pluginSlug AND ver = @version
            LIMIT 1
            """,
            new
            {
                pluginSlug,
                version = manifest.Version.VersionParts
            });

        Assert.Equal(new[] { 2, 0, 0, 0 }, minRow.EffectiveMin);
        Assert.True(minRow.OverrideEnabled);
        Assert.Equal(new[] { 2, 5, 0, 0 }, maxRow.EffectiveMax);
        Assert.True(maxRow.OverrideEnabled);

        await conn.SetPluginSettings(pluginSlug, new PluginSettings
        {
            PluginTitle = pluginSlug,
            Description = "Override persistence test plugin",
            GitRepository = ServerTester.RepoUrl
        }, PluginVisibilityEnum.Listed);

        var outputCacheStore = tester.GetService<IOutputCacheStore>();
        await outputCacheStore.EvictByTagAsync(CacheTags.Plugins, CancellationToken.None);

        var client = tester.CreateHttpClient();
        Assert.Empty(await client.GetPublishedVersions("1.9.9.9", true));
        Assert.Single(await client.GetPublishedVersions("2.0.0.0", true));
        Assert.Single(await client.GetPublishedVersions("2.5.0.0", true));
        Assert.Empty(await client.GetPublishedVersions("2.5.0.1", true));
    }

    private static string CreateManifest(string btcpayCondition)
    {
        return $$"""
                 {
                   "Identifier": "BTCPayServer.Plugins.TestCompat",
                   "Name": "Test Compat",
                   "Version": "1.0.0",
                   "Description": "Test plugin",
                   "Dependencies": [
                     {
                       "Identifier": "BTCPayServer",
                       "Condition": "{{btcpayCondition}}"
                     }
                   ]
                 }
                 """;
    }
}
