using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Playwright.Xunit;
using Npgsql;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.DataModels;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.PluginTests;

[Collection("Playwright Tests")]
public class CreatePluginUITests(ITestOutputHelper output) : PageTest
{
    private readonly XUnitLogger _log = new("CreatePluginUITests", output);

    [Fact]
    public async Task InvalidLogoValidationDoesNotReserveSlug()
    {
        await using var t = new PlaywrightTester(_log);
        t.Server.ReuseDatabase = false;
        await t.StartAsync();
        await using var conn = await t.Server.GetService<DBConnectionFactory>().Open();

        await PrepareVerifiedPublisherAsync(t, conn);

        var pluginSlug = "failed-create-" + PlaywrightTester.GetRandomUInt256()[..8];
        var oversizedImage = Path.Combine(Path.GetTempPath(), $"oversized-{Guid.NewGuid():N}.png");
        CreateOversizedPng(oversizedImage);

        try
        {
            await t.GoToUrl("/plugins/create");
            await t.Page.Locator("#PluginSlug").FillAsync(pluginSlug);
            await t.Page.Locator("#PluginTitle").FillAsync("Failed create test");
            await t.Page.Locator("#Description").FillAsync("Slug should stay available after failed validation.");
            await t.Page.Locator("#Logo").SetInputFilesAsync(oversizedImage);
            await t.Page.Locator("#Create").ClickAsync();

            await Expect(t.Page.Locator("span[data-valmsg-for='Logo']")).ToContainTextAsync("Image upload validation failed");

            var pluginCount = await conn.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM plugins WHERE slug = @Slug",
                new { Slug = pluginSlug });
            Assert.Equal(0, pluginCount);

            await t.Page.Locator("#Logo").SetInputFilesAsync(Array.Empty<string>());
            await t.Page.Locator("#Create").ClickAsync();
            await Expect(t.Page).ToHaveURLAsync(new Regex($"/plugins/{Regex.Escape(pluginSlug)}$", RegexOptions.IgnoreCase));
            await t.AssertNoError();

            pluginCount = await conn.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM plugins WHERE slug = @Slug",
                new { Slug = pluginSlug });
            Assert.Equal(1, pluginCount);
        }
        finally
        {
            if (File.Exists(oversizedImage))
                File.Delete(oversizedImage);
        }
    }

    private static void CreateOversizedPng(string path)
    {
        var bytes = new byte[1_100_000];
        bytes[0] = 0x89;
        bytes[1] = 0x50;
        bytes[2] = 0x4E;
        bytes[3] = 0x47;
        bytes[4] = 0x0D;
        bytes[5] = 0x0A;
        bytes[6] = 0x1A;
        bytes[7] = 0x0A;
        File.WriteAllBytes(path, bytes);
    }

    private ServerTester CreateServerWithTestStorage(bool throwOnUpload = false)
    {
        var server = new ServerTester("PlaywrightTest", _log);
        server.ConfigureServices = services =>
        {
            services.RemoveAll<AzureStorageClient>();
            services.AddSingleton<TestAzureStorageClient>(sp => new TestAzureStorageClient(
                sp.GetRequiredService<ProcessRunner>(),
                sp.GetRequiredService<IConfiguration>())
            {
                ThrowOnUpload = throwOnUpload
            });
            services.AddSingleton<AzureStorageClient>(sp => sp.GetRequiredService<TestAzureStorageClient>());
        };

        return server;
    }

    private async Task PrepareVerifiedPublisherAsync(PlaywrightTester tester, NpgsqlConnection conn)
    {
        await conn.SettingsSetAsync(SettingsKeys.VerifiedGithub, "true");
        var verifiedCache = tester.Server.GetService<AdminSettingsCache>();
        await verifiedCache.RefreshAllAdminSettings(conn);

        await tester.GoToUrl("/register");
        var user = await tester.RegisterNewUser();
        await Expect(tester.Page!).ToHaveURLAsync(new Regex(".*/dashboard$", RegexOptions.IgnoreCase));
        await tester.VerifyUserAccounts(user);
    }

    private sealed class TestAzureStorageClient(ProcessRunner processRunner, IConfiguration configuration)
        : AzureStorageClient(processRunner, configuration)
    {
        public bool ThrowOnUpload { get; init; }
        public List<string> UploadedBlobNames { get; } = [];
        public List<string> DeletedBlobNames { get; } = [];

        public override Task<string> UploadImageFile(IFormFile file, string blobName)
        {
            if (ThrowOnUpload)
                throw new AzureStorageClientException("Synthetic upload failure");

            UploadedBlobNames.Add(blobName);
            return Task.FromResult($"https://example.com/{blobName}");
        }

        public override Task DeleteImageFileIfExists(string blobName)
        {
            DeletedBlobNames.Add(blobName);
            return Task.CompletedTask;
        }
    }
}
