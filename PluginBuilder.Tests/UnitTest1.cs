using System.IO.Compression;
using Microsoft.AspNetCore.Http;
using Dapper;
using Microsoft.AspNetCore.OutputCaching;
using Newtonsoft.Json;
using PluginBuilder.APIModels;
using PluginBuilder.DataModels;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

using PluginBuilder.Builds.Services;

namespace PluginBuilder.Tests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class UnitTest1 : UnitTestBase
{
    public UnitTest1(ITestOutputHelper logs) : base(logs)
    {
    }

    [Fact]
    [Trait("Category", "ExecutorIntegration")]
    public async Task ExecutorStartupBecomesReady()
    {
        await using var tester = await Start();
        var state = tester.GetService<BuildExecutorState>().Snapshot;

        Assert.True(state.IsReady, state.UnavailableReason);
        Assert.False(string.IsNullOrWhiteSpace(state.WorkerImageId));
        Assert.False(string.IsNullOrWhiteSpace(state.ProxyImageId));
        Assert.Null(state.UnavailableReason);
    }

    [Fact]
    public async Task PluginsSearchWithNullByte_DoesNotReturnServerError()
    {
        await using var tester = Create();
        tester.ReuseDatabase = false;
        await tester.Start();

        var ownerId = await tester.CreateFakeUserAsync();
        await tester.CreatePublishedPluginAsync(ownerId);

        var client = tester.CreateHttpClient();
        var urls = new[]
        {
            "/public/plugins?searchPluginName=%00",
            "/api/v1/plugins?searchPluginName=%00"
        };

        foreach (var url in urls)
        {
            var response = await client.GetAsync(url);
            Assert.True(response.IsSuccessStatusCode,
                $"Expected successful response for {url}, but got {(int)response.StatusCode} ({response.StatusCode}).");
        }
    }

    [Theory]
    [InlineData("test-6", true)]
    [InlineData("test-6-", false)]
    [InlineData("6test-6", false)]
    [InlineData("-test-6", false)] 
    [InlineData("te", false)]
    [InlineData("teqoeteqoeteqoeteqoeteqoeteqoee", false)]
    [InlineData("teqoeteqoeteqoeteqoeteqoet", true)]
    public void IsValidSlugTest(string slug, bool expected)
    {
        Assert.Equal(expected, PluginSlug.IsValidSlugName(slug));
    }

    [Fact]
    [Trait("Category", "ExecutorIntegration")]
    public async Task CanPackPlugin()
    {
        await using var tester = Create();
        tester.ReuseDatabase = false;
        await tester.Start();

        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        //https://github.com/NicolasDorier/btcpayserver/tree/plugins/collection2/Plugins/BTCPayServer.Plugins.RockstarStylist
        var ownerId = await tester.CreateFakeUserAsync();
        var fullBuildId = await tester.CreateAndBuildPluginAsync(ownerId);
        var pluginSlug = fullBuildId.PluginSlug.ToString();

        var client = tester.CreateHttpClient();
        var versions = await client.GetPublishedVersions("1.4.6.0", true);
        var version = Assert.Single(versions);
        Assert.NotNull(version);
        var prev = version;
        version = await client.GetPlugin(version.ProjectSlug, version.Version);
        Assert.NotNull(version);
        Assert.Equal(JsonConvert.SerializeObject(version), JsonConvert.SerializeObject(prev));

        Assert.Null(await client.GetPlugin(version.ProjectSlug, "10.0.0.1"));
        Assert.Equal("1.0.2.0", version.Version);
        versions = await client.GetPublishedVersions("1.4.5.9", true);
        Assert.Empty(versions);
        versions = await client.GetPublishedVersions("1.4.6.0", false);
        Assert.Empty(versions);

        // Can download the project?
        var b1 = await client.DownloadPlugin(new PluginSelectorBySlug(pluginSlug), PluginVersion.Parse("1.0.2.0"));
        var b2 = await client.DownloadPlugin(new PluginSelectorByIdentifier("BTCPayServer.Plugins.RockstarStylist"), PluginVersion.Parse("1.0.2.0"));
        Assert.NotNull(b1);
        Assert.NotNull(b2);
        Assert.Equal(b1.Length, b2.Length);


        var manifest = PluginManifest.Parse(version.ManifestInfo.ToString());

        // Nothing changed
        Assert.False(await conn.SetVersionBuild(fullBuildId, manifest.Version, manifest.BTCPayMinVersion, manifest.BTCPayMaxVersion, true));
        // Can change BTCPayMinVersion
        Assert.True(await conn.SetVersionBuild(fullBuildId, manifest.Version, null, manifest.BTCPayMaxVersion, true));
        // Can remove pre-release
        Assert.True(await conn.SetVersionBuild(fullBuildId, manifest.Version, manifest.BTCPayMinVersion, manifest.BTCPayMaxVersion, false));

        // Can't put back in pre-release
        Assert.False(await conn.SetVersionBuild(fullBuildId, manifest.Version, manifest.BTCPayMinVersion, manifest.BTCPayMaxVersion, true));
        // Can't modify pre-release
        Assert.False(await conn.SetVersionBuild(fullBuildId, manifest.Version, null, manifest.BTCPayMaxVersion, false));


        // Another plugin slug try to hijack the package
        await tester.CreateAndBuildPluginAsync(
            ownerId,
            ServerTester.CreatePluginSlug(),
            "plugins/collection2",
            "Plugins/BTCPayServer.Plugins.RockstarStylist"
        );

        var rockstarPlugins =
            await conn.QueryAsync<string?>("SELECT slug FROM plugins WHERE identifier='BTCPayServer.Plugins.RockstarStylist'");
        var p = Assert.Single(rockstarPlugins);
        Assert.Equal(pluginSlug, p);
        versions = await client.GetPublishedVersions("1.4.6.0", true);
        version = Assert.Single(versions);
        Assert.Equal(pluginSlug, version.ProjectSlug);

        // Let's see what happen if there is two versions of the same plugin
        await conn.ExecuteAsync("""
                                INSERT INTO versions (plugin_slug, ver, build_id, btcpay_min_ver, btcpay_max_ver, pre_release, updated_at, signatureproof)
                                VALUES (@pluginSlug, ARRAY[1,0,2,1], @buildId, ARRAY[1,4,6,0], NULL, 'f', CURRENT_TIMESTAMP, NULL)
                                """, new { pluginSlug, buildId = fullBuildId.BuildId });
        var outputCacheStore = tester.GetService<IOutputCacheStore>();
        await outputCacheStore.EvictByTagAsync(CacheTags.Plugins, CancellationToken.None);
        versions = await client.GetPublishedVersions("1.4.6.0", true);
        version = Assert.Single(versions);
        Assert.Equal("1.0.2.1", version.Version);
        versions = await client.GetPublishedVersions("1.4.6.0", true, true);
        Assert.Equal("1.0.2.1", versions[1].Version);
        Assert.Equal("1.0.2.0", versions[0].Version);

        // listed - always render
        await conn.ExecuteAsync("UPDATE plugins SET visibility = 'listed' WHERE slug = @pluginSlug", new { pluginSlug });
        await outputCacheStore.EvictByTagAsync(CacheTags.Plugins, CancellationToken.None);
        var res = await client.GetPublishedVersions("2.1.0.0", false);
        Assert.Contains(res, p => p.ProjectSlug == pluginSlug);

        // unlisted - only render with compatible search term or legacy versions
        await conn.ExecuteAsync("UPDATE plugins SET visibility = 'unlisted' WHERE slug = @pluginSlug", new { pluginSlug });
        await outputCacheStore.EvictByTagAsync(CacheTags.Plugins, CancellationToken.None);
        res = await client.GetPublishedVersions("2.1.0.0", false);
        Assert.DoesNotContain(res, p => p.ProjectSlug == pluginSlug);

        res = await client.GetPublishedVersions("2.1.0.0", false, searchPluginName: "rockstar");
        Assert.Contains(res, p => p.ProjectSlug == pluginSlug);

        var raw = await client.GetStringAsync("/api/v1/plugins");
        var legacyRes = JsonConvert.DeserializeObject<PublishedVersion[]>(raw);
        Assert.Contains(legacyRes, p => p.ProjectSlug == pluginSlug);

        // hidden - never render
        await conn.ExecuteAsync("UPDATE plugins SET visibility = 'hidden' WHERE slug = @pluginSlug", new { pluginSlug });
        await outputCacheStore.EvictByTagAsync(CacheTags.Plugins, CancellationToken.None);
        res = await client.GetPublishedVersions("2.1.0.0", false);
        Assert.DoesNotContain(res, p => p.ProjectSlug == pluginSlug);

        res = await client.GetPublishedVersions("2.1.0.0", false, searchPluginName: "rockstar");
        Assert.DoesNotContain(res, p => p.ProjectSlug == pluginSlug);
    }
    [Fact]
    public Task DownloadEndpoint_UsesInternalLoopbackRedirectWhenLocalArtifactProxyEnabled()
        => AssertArtifactDownloadAsync(useProxy: true);

    [Fact]
    public Task DownloadEndpoint_DoesNotUseInternalLoopbackRedirectWhenLocalArtifactProxyDisabled()
        => AssertArtifactDownloadAsync(useProxy: false);

    private async Task AssertArtifactDownloadAsync(bool useProxy)
    {
        await using var tester = Create();
        tester.ReuseDatabase = false;
        tester.EnableLocalArtifactDownloadProxy = useProxy;
        await tester.Start();

        var ownerId = await tester.CreateFakeUserAsync();
        var slug = "download-" + Guid.NewGuid().ToString("N")[..16];
        var fullBuildId = await tester.CreatePublishedPluginAsync(ownerId, slug);

        // These tests exercise delivery, not compilation. Store a real ZIP with known bytes.
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("download-test.txt").Open());
            writer.Write("Artifact download test payload");
        }
        var expectedBytes = stream.ToArray();
        var storage = tester.GetService<AzureStorageClient>();
        var blobName = $"{slug}/download-test.btcpay";

        try
        {
            var url = await storage.UploadImageFile(new FormFile(stream, 0, stream.Length, "artifact", "download-test.btcpay")
            {
                Headers = new HeaderDictionary(),
                ContentType = "application/zip"
            }, blobName);
            await using var conn = await tester.GetService<DBConnectionFactory>().Open();
            Assert.Equal(1, await conn.ExecuteAsync(
                """
                UPDATE builds
                SET build_info = jsonb_set(build_info, '{url}', to_jsonb(CAST(@url AS text)))
                WHERE plugin_slug = @slug AND id = @buildId
                """,
                new { url, slug, buildId = fullBuildId.BuildId }));

            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            client.BaseAddress = new Uri(tester.WebApp.Urls.First(), UriKind.Absolute);
            using var response = await client.GetAsync($"api/v1/plugins/{slug}/versions/1.0.2.0/download");

            Assert.Equal(System.Net.HttpStatusCode.Found, response.StatusCode);
            Assert.NotNull(response.Headers.Location);
            if (useProxy)
            {
                Assert.Equal($"/api/v1/plugins/{slug}/versions/1.0.2.0/download-loopback",
                    response.Headers.Location!.OriginalString);
            }
            else
            {
                Assert.DoesNotContain("download-loopback", response.Headers.Location!.OriginalString, StringComparison.Ordinal);
                Assert.True(response.Headers.Location.IsAbsoluteUri);
                Assert.True(response.Headers.Location.IsLoopback);
                Assert.Equal(new Uri(url), response.Headers.Location);
            }

            using var download = await client.GetAsync(response.Headers.Location);
            Assert.Equal(System.Net.HttpStatusCode.OK, download.StatusCode);
            Assert.Equal("application/zip", download.Content.Headers.ContentType?.MediaType);
            Assert.Equal(expectedBytes, await download.Content.ReadAsByteArrayAsync());
        }
        finally
        {
            await storage.DeleteImageFileIfExists(blobName);
        }
    }

}
