using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit;
using Npgsql;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.PluginTests;

[Collection("Playwright Tests")]
public class PluginTitleUITests(ITestOutputHelper output) : PageTest
{
    private readonly XUnitLogger _log = new("PluginTitleUI", output);
    private const string DuplicateMessage = "This plugin title is already in use. Please choose a different title.";

    [Theory]
    [InlineData("create")]
    [InlineData("owner")]
    [InlineData("admin")]
    public async Task TitleLengthIsValidatedBeforeUploadsAndAfterTrimming(string operation)
    {
        const string existingImage = "https://example.invalid/title-length.png";
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        UseTrackingStorage(tester);
        await tester.StartAsync();
        var ownerId = await SignInVerifiedAdmin(tester);
        var storage = tester.Server.GetService<TrackingImageStorage>();
        await using var conn = await tester.Server.GetService<DBConnectionFactory>().Open();
        var slug = "title-length-" + operation;
        var page = tester.Page!;
        var field = operation == "admin" ? "PluginSettings.PluginTitle" : "PluginTitle";
        var input = operation == "admin" ? "#PluginSettings_PluginTitle" : "#PluginTitle";
        var form = operation switch { "create" => "form:has(#PluginSlug)", "owner" => "#plugin-setting-form", _ => "#plugin-edit-form" };
        if (operation == "create")
            await FillCreation(tester, slug, "Short title");
        else
        {
            await conn.NewPlugin(slug, ownerId);
            await conn.SetPluginSettings(slug, new PluginSettings
            {
                PluginTitle = "Short title", Description = "Keep this description", GitRepository = ServerTester.RepoUrl,
                Images = [existingImage]
            });
            await tester.GoToUrl(operation == "owner" ? $"/plugins/{slug}/settings" : $"/admin/plugins/edit/{slug}");
        }

        var image = Path.Combine(Path.GetTempPath(), $"title-length-{Guid.NewGuid():N}.png");
        tester.CreateTestImage(image);
        try
        {
            await page.Locator(operation == "admin" ? "#LogoFile" : "#Logo").SetInputFilesAsync(image);
            // Exercise the server even when client-side maxlength validation is bypassed.
            await page.Locator(input).EvaluateAsync("(input, title) => input.value = title", new string('x', 501));
            var response = await SubmitWithoutClientValidation(page, form);
            Assert.True(response.Status == 200, $"Expected field validation; HTTP {response.Status}, uploaded files: {storage.Uploaded.Count}.");
            await Expect(page.Locator($"[data-valmsg-for='{field}']")).ToHaveTextAsync("Plugin title must be 500 characters or fewer.");
            Assert.Empty(storage.Uploaded);
            if (operation != "create")
            {
                Assert.Equal("Short title", (await conn.GetSettings(slug))!.PluginTitle);
                await Expect(page.Locator($"#images-order-list img[src='{existingImage}']")).ToHaveCountAsync(1);
            }

            // PostgreSQL counts Unicode code points, so a supplementary character counts once.
            var title = string.Concat(Enumerable.Repeat("𠮷", 500));
            await page.Locator(input).EvaluateAsync("(input, title) => input.value = title", "  " + title + "  ");
            response = await SubmitWithoutClientValidation(page, form);
            Assert.Equal(302, response.Status);
            Assert.Equal(title, (await conn.GetSettings(slug))!.PluginTitle);
            if (operation != "create")
                Assert.Equal(new[] { existingImage }, (await conn.GetSettings(slug))!.Images);
        }
        finally { File.Delete(image); }
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "\t\u00a0")]
    public async Task AdminRequiresATitleBeforeUploadsAndRetryPreservesMedia(bool published, string rejectedTitle)
    {
        const string existingImage = "https://example.invalid/title-published.png";
        const string existingLogo = "https://example.invalid/title-published-logo.png";
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        UseTrackingStorage(tester);
        await tester.StartAsync();
        var ownerId = await SignInVerifiedAdmin(tester);
        if (published)
            await tester.Server.CreatePublishedPluginAsync(ownerId, "title-published");
        await using var conn = await tester.Server.GetService<DBConnectionFactory>().Open();
        if (!published)
            await conn.NewPlugin("title-published", ownerId);
        var settings = new PluginSettings
        {
            PluginTitle = "Current public title",
            Images = [existingImage],
            Logo = existingLogo
        };
        await conn.SetPluginSettings("title-published", settings);
        var page = tester.Page!;
        await tester.GoToUrl("/admin/plugins/edit/title-published");
        await page.Locator("#PluginSettings_PluginTitle").FillAsync(rejectedTitle);
        var image = Path.Combine(Path.GetTempPath(), $"title-clear-{Guid.NewGuid():N}.png");
        tester.CreateTestImage(image);
        try
        {
            await page.Locator("#LogoFile").SetInputFilesAsync(image);
            await page.Locator("#images-input").SetInputFilesAsync(image);
            await SubmitWithoutClientValidation(page, "#plugin-edit-form");
        }
        finally { File.Delete(image); }
        await Expect(page.Locator("[data-valmsg-for='PluginSettings.PluginTitle']")).ToHaveTextAsync(
            "Plugin title is required.");
        Assert.Equal("Current public title", (await conn.GetSettings("title-published"))!.PluginTitle);
        Assert.Empty(tester.Server.GetService<TrackingImageStorage>().Uploaded);
        await Expect(page.Locator($"#images-order-list img[src='{existingImage}']")).ToHaveCountAsync(1);
        await Expect(page.Locator($"#plugin-edit-form img[src='{existingLogo}']")).ToHaveCountAsync(1);

        await page.Locator("#PluginSettings_PluginTitle").FillAsync("  New public title  ");
        await page.Locator("#plugin-edit-form input[type='submit']").ClickAsync();
        await Expect(page.Locator(".alert-success")).ToContainTextAsync("Plugin settings updated successfully");
        if (published)
        {
            using var client = tester.Server.CreateHttpClient();
            var response = Newtonsoft.Json.Linq.JObject.Parse(await client.GetStringAsync("/api/v1/plugins/directory/title-published?includePreRelease=true"));
            Assert.Equal("New public title", response.Value<string>("pluginTitle"));
            Assert.Equal("Rockstar Stylist", response["manifestInfo"]!.Value<string>("Name"));
        }
        settings = (await conn.GetSettings("title-published"))!;
        Assert.Equal(new[] { existingImage }, settings.Images);
        Assert.Equal(existingLogo, settings.Logo);
    }

    [Fact]
    public async Task CreationAndOwnerAndAdminEditsNormalizeTitlesAndAllowTheirOwnTitle()
    {
        const string firstImage = "https://example.invalid/title-first.png";
        const string removedImage = "https://example.invalid/title-remove.png";
        const string lastImage = "https://example.invalid/title-last.png";
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        UseTrackingStorage(tester);
        await tester.StartAsync();
        var ownerId = await SignInVerifiedAdmin(tester);
        var page = tester.Page!;
        await using var conn = await tester.Server.GetService<DBConnectionFactory>().Open();

        await FillCreation(tester, "title-created", "  Café  Payments  ");
        var image = Path.Combine(Path.GetTempPath(), $"title-create-{Guid.NewGuid():N}.png");
        tester.CreateTestImage(image);
        try
        {
            await page.Locator("#Logo").SetInputFilesAsync(image);
            await page.Locator("#Create").ClickAsync();
            await Expect(page).ToHaveURLAsync(new Regex("/plugins/title-created$"));
        }
        finally { File.Delete(image); }
        Assert.Equal("Café  Payments", (await conn.GetSettings("title-created"))!.PluginTitle);
        var storage = tester.Server.GetService<TrackingImageStorage>();
        Assert.Equal("https://example.invalid/" + Assert.Single(storage.Uploaded),
            (await conn.GetSettings("title-created"))!.Logo);
        Assert.Empty(storage.Deleted);

        await FillCreation(tester, "title-duplicate", "  CAFÉ  PAYMENTS  ");
        await page.Locator("#Create").ClickAsync();
        await Expect(page.Locator("[data-valmsg-for='PluginTitle']")).ToHaveTextAsync(DuplicateMessage);
        Assert.False(await conn.QuerySingleAsync<bool>("SELECT EXISTS(SELECT 1 FROM plugins WHERE slug='title-duplicate')"));

        Assert.True(await conn.NewPlugin("title-other", ownerId));
        Assert.True(await conn.SetPluginSettings("title-other", new PluginSettings { PluginTitle = "Other title" }));

        await tester.GoToUrl("/plugins/title-created/settings");
        await page.Locator("#GitRepository").FillAsync(ServerTester.RepoUrl);
        await page.Locator("#PluginTitle").FillAsync("  CAFÉ  Payments  ");
        await page.Locator("button[form='plugin-setting-form']").ClickAsync();
        await Expect(page.Locator(".alert-success")).ToContainTextAsync("Settings updated successfully");
        Assert.Equal("CAFÉ  Payments", (await conn.GetSettings("title-created"))!.PluginTitle);

        await page.Locator("#PluginTitle").FillAsync("  OTHER TITLE  ");
        await page.Locator("button[form='plugin-setting-form']").ClickAsync();
        await Expect(page.Locator("[data-valmsg-for='PluginTitle']")).ToHaveTextAsync(DuplicateMessage);
        Assert.Equal("CAFÉ  Payments", (await conn.GetSettings("title-created"))!.PluginTitle);

        await page.Locator("#PluginTitle").FillAsync("   ");
        await page.Locator("button[form='plugin-setting-form']").ClickAsync();
        await Expect(page.Locator("[data-valmsg-for='PluginTitle']")).ToContainTextAsync("required");
        Assert.Equal("CAFÉ  Payments", (await conn.GetSettings("title-created"))!.PluginTitle);

        var settings = (await conn.GetSettings("title-created"))!;
        settings.Images = [firstImage, removedImage, lastImage];
        await conn.SetPluginSettings("title-created", settings);
        await tester.GoToUrl("/admin/plugins/edit/title-created");
        await page.Locator("#PluginSettings_PluginTitle").FillAsync("  Café  payments  ");
        await page.Locator("#plugin-edit-form input[type='submit']").ClickAsync();
        await Expect(page.Locator(".alert-success")).ToContainTextAsync("Plugin settings updated successfully");
        Assert.Equal("Café  payments", (await conn.GetSettings("title-created"))!.PluginTitle);

        await page.Locator("#PluginSettings_PluginTitle").FillAsync("\tOther title ");
        await page.Locator("#images-order-list").EvaluateAsync("list => list.prepend(list.lastElementChild)");
        await page.Locator($"button[name='removeImageUrl'][value='{removedImage}']").ClickAsync();
        await Expect(page.Locator("[data-valmsg-for='PluginSettings.PluginTitle']")).ToHaveTextAsync(DuplicateMessage);
        Assert.Equal("Café  payments", (await conn.GetSettings("title-created"))!.PluginTitle);
        Assert.Equal(new[] { lastImage, firstImage }, await page.Locator("#images-order-list input[name='ImagesUrl']").EvaluateAllAsync<string[]>(
            "inputs => inputs.map(input => input.value)"));
        Assert.Equal(new[] { firstImage, removedImage, lastImage }, (await conn.GetSettings("title-created"))!.Images);

        await page.Locator("#PluginSettings_PluginTitle").FillAsync("Café  payments");
        await page.Locator("#plugin-edit-form input[type='submit']").ClickAsync();
        await Expect(page.Locator(".alert-success")).ToContainTextAsync("Plugin settings updated successfully");
        Assert.Equal(new[] { lastImage, firstImage }, (await conn.GetSettings("title-created"))!.Images);
    }

    [Theory]
    [InlineData("create", false)]
    [InlineData("owner", false)]
    [InlineData("admin", false)]
    [InlineData("create", true)]
    [InlineData("owner", true)]
    [InlineData("admin", true)]
    public async Task ConcurrentTitleClaimsShowValidationAndPreserveTheLosingPlugin(string operation, bool withUploads)
    {
        const string existingLogo = "https://example.invalid/existing-logo.png";
        const string keptImage = "https://example.invalid/existing-keep.png";
        const string removedImage = "https://example.invalid/existing-remove.png";
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        UseTrackingStorage(tester);
        await tester.StartAsync();
        var ownerId = await SignInVerifiedAdmin(tester);
        await using var observer = await tester.Server.GetService<DBConnectionFactory>().Open();
        foreach (var slug in new[] { "title-winner", "title-edited" })
        {
            Assert.True(await observer.NewPlugin(slug, ownerId));
            Assert.True(await observer.SetPluginSettings(slug, new PluginSettings
            {
                PluginTitle = slug, Description = "Preserved description", GitRepository = ServerTester.RepoUrl,
                Logo = withUploads && slug == "title-edited" ? existingLogo : null,
                Images = withUploads && slug == "title-edited" ? [keptImage, removedImage] : []
            }));
        }

        var page = tester.Page!;
        string field;
        string submit;
        if (operation == "create")
        {
            await FillCreation(tester, "title-new", "  CAFÉ  RACE  ");
            field = "PluginTitle";
            submit = "#Create";
        }
        else if (operation == "owner")
        {
            await tester.GoToUrl("/plugins/title-edited/settings");
            await page.Locator("#PluginTitle").FillAsync("  CAFÉ  RACE  ");
            field = "PluginTitle";
            submit = "button[form='plugin-setting-form']";
        }
        else
        {
            await tester.GoToUrl("/admin/plugins/edit/title-edited");
            await page.Locator("#PluginSettings_PluginTitle").FillAsync("  CAFÉ  RACE  ");
            field = "PluginSettings.PluginTitle";
            submit = "#plugin-edit-form input[type='submit']";
        }

        if (withUploads && operation != "create")
        {
            await Expect(page.Locator($"#images-order-list img[src='{keptImage}']")).ToHaveCountAsync(1);
            await Expect(page.Locator($"#images-order-list img[src='{removedImage}']")).ToHaveCountAsync(1);
            // Removing an existing image submits the same form, including title and new files.
            submit = $"button[name='removeImageUrl'][value='{removedImage}']";
        }

        await using var winner = await tester.Server.GetService<DBConnectionFactory>().Open();
        await using var transaction = await winner.BeginTransactionAsync();
        await winner.SetPluginSettings("title-winner", new PluginSettings { PluginTitle = "Café  Race" });

        if (withUploads)
        {
            var image = Path.Combine(Path.GetTempPath(), $"title-race-{Guid.NewGuid():N}.png");
            tester.CreateTestImage(image);
            try
            {
                var payload = new FilePayload { Name = "title-race.png", MimeType = "image/png", Buffer = File.ReadAllBytes(image) };
                await page.Locator(operation == "admin" ? "#LogoFile" : "#Logo").SetInputFilesAsync(payload);
                if (operation != "create")
                    await page.Locator("#images-input").SetInputFilesAsync(payload);
            }
            finally { File.Delete(image); }
        }
        var request = page.Locator(submit).ClickAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!await observer.QuerySingleAsync<bool>("""
                   SELECT EXISTS(SELECT 1 FROM pg_stat_activity
                   WHERE datname=current_database() AND @Pid=ANY(pg_blocking_pids(pid)))
                   """, new { Pid = winner.ProcessID }))
            await Task.Delay(10, timeout.Token);
        await transaction.CommitAsync();
        await request;

        await Expect(page.Locator($"[data-valmsg-for='{field}']")).ToHaveTextAsync(DuplicateMessage);
        if (withUploads)
        {
            var storage = tester.Server.GetService<TrackingImageStorage>();
            Assert.Equal(operation == "create" ? 1 : 2, storage.Uploaded.Count);
            Assert.Equal(storage.Uploaded.Order(), storage.Deleted.Order());
            await Expect(page.Locator("body")).ToContainTextAsync("New uploads were not saved. Please select them again.");
            var unchanged = (await observer.GetSettings("title-edited"))!;
            Assert.Equal(new[] { keptImage, removedImage }, unchanged.Images);
            Assert.Equal(existingLogo, unchanged.Logo);
            var html = await page.ContentAsync();
            foreach (var uploaded in storage.Uploaded)
                Assert.DoesNotContain("https://example.invalid/" + uploaded, html);
            if (operation != "create")
            {
                var editForm = operation == "owner" ? "#plugin-setting-form" : "#plugin-edit-form";
                await Expect(page.Locator($"{editForm} img[src='{existingLogo}']")).ToHaveCountAsync(1);
                await Expect(page.Locator($"#images-order-list img[src='{keptImage}']")).ToHaveCountAsync(1);
                await Expect(page.Locator($"#images-order-list img[src='{removedImage}']")).ToHaveCountAsync(0);
            }
        }
        await Expect(page.Locator("[data-testid='ui-error-page']")).ToHaveCountAsync(0);
        Assert.Equal("title-edited", (await observer.GetSettings("title-edited"))!.PluginTitle);
        Assert.False(await observer.QuerySingleAsync<bool>("SELECT EXISTS(SELECT 1 FROM plugins WHERE slug='title-new')"));
        Assert.Equal(1, await observer.QuerySingleAsync<int>("""
            SELECT count(*) FROM plugins
            WHERE lower(nullif(btrim(settings->>'pluginTitle'), '') COLLATE "pg_c_utf8") =
                  lower('CAFÉ  RACE' COLLATE "pg_c_utf8")
            """));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedCreationCleansOnlyTheNewLogoAndPreservesTheExistingPlugin(bool databaseFailure)
    {
        const string existingLogo = "https://example.invalid/existing-logo.png";
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        UseTrackingStorage(tester);
        await tester.StartAsync();
        await SignInVerifiedAdmin(tester);
        var ownerId = await tester.Server.CreateFakeUserAsync();
        await using var conn = await tester.Server.GetService<DBConnectionFactory>().Open();
        await conn.NewPlugin("title-slug-taken", ownerId);
        await conn.SetPluginSettings("title-slug-taken", new PluginSettings { PluginTitle = "Existing title", Logo = existingLogo });
        if (databaseFailure)
            await conn.ExecuteAsync("ALTER TABLE plugins ADD CONSTRAINT reject_test_creation CHECK (slug <> 'title-db-failure')");
        await FillCreation(tester, databaseFailure ? "title-db-failure" : "title-slug-taken", "Different title");
        var image = Path.Combine(Path.GetTempPath(), $"title-slug-{Guid.NewGuid():N}.png");
        tester.CreateTestImage(image);
        IResponse response;
        try
        {
            await tester.Page!.Locator("#Logo").SetInputFilesAsync(image);
            response = await tester.Page.RunAndWaitForResponseAsync(
                () => tester.Page.Locator("#Create").ClickAsync(),
                r => r.Request.Method == "POST" && r.Url.EndsWith("/plugins/create", StringComparison.Ordinal));
        }
        finally { File.Delete(image); }

        Assert.Equal(databaseFailure ? 500 : 200, response.Status);
        if (databaseFailure)
        {
            Assert.False(await conn.QuerySingleAsync<bool>("SELECT EXISTS(SELECT 1 FROM plugins WHERE slug='title-db-failure')"));
            Assert.Empty(await conn.RetrievePluginUserIds("title-db-failure"));
        }
        else
            await Expect(tester.Page!.Locator("[data-valmsg-for='PluginSlug']")).ToHaveTextAsync("This slug already exists");
        var storage = tester.Server.GetService<TrackingImageStorage>();
        Assert.Single(storage.Uploaded);
        Assert.Equal(storage.Uploaded, storage.Deleted);
        var unchanged = (await conn.GetSettings("title-slug-taken"))!;
        Assert.Equal("Existing title", unchanged.PluginTitle);
        Assert.Equal(existingLogo, unchanged.Logo);
        Assert.Equal(ownerId, Assert.Single(await conn.RetrievePluginUserIds("title-slug-taken")));
    }

    private static async Task FillCreation(PlaywrightTester tester, string slug, string title)
    {
        await tester.GoToUrl("/plugins/create");
        await tester.Page!.Locator("#PluginSlug").FillAsync(slug);
        await tester.Page.Locator("#PluginTitle").FillAsync(title);
        await tester.Page.Locator("#Description").FillAsync("Plugin title validation.");
    }

    private static async Task<string> SignInVerifiedAdmin(PlaywrightTester tester)
    {
        var email = await tester.CreateServerAdminAsync("title-admin");
        await using var conn = await tester.Server.GetService<DBConnectionFactory>().Open();
        await tester.EnableGithubVerificationAsync(conn);
        await tester.VerifyUserAccounts(email);
        await tester.LogIn(email);
        return await conn.QuerySingleAsync<string>("SELECT \"Id\" FROM \"AspNetUsers\" WHERE \"Email\"=@Email", new { Email = email });
    }

    private static async Task<IResponse> SubmitWithoutClientValidation(IPage page, string selector)
    {
        var form = page.Locator(selector);
        var action = await form.EvaluateAsync<string>("form => form.action");
        return await page.RunAndWaitForResponseAsync(
            () => form.EvaluateAsync("form => HTMLFormElement.prototype.submit.call(form)"),
            response => response.Request.Method == "POST" && response.Url == action);
    }

    private static void UseTrackingStorage(PlaywrightTester tester) => tester.Server.ConfigureServices = services =>
    {
        services.RemoveAll<AzureStorageClient>();
        services.AddSingleton<TrackingImageStorage>();
        services.AddSingleton<AzureStorageClient>(sp => sp.GetRequiredService<TrackingImageStorage>());
    };

    private sealed class TrackingImageStorage(IConfiguration configuration) : AzureStorageClient(configuration)
    {
        public List<string> Uploaded { get; } = [];
        public List<string> Deleted { get; } = [];
        public override Task<string> UploadImageFile(IFormFile file, string blobName)
        {
            Uploaded.Add(blobName);
            return Task.FromResult("https://example.invalid/" + blobName);
        }
        public override Task DeleteImageFileIfExists(string blobName)
        {
            Deleted.Add(blobName);
            return Task.CompletedTask;
        }
    }
}
