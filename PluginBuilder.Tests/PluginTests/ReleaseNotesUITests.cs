using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit;
using Npgsql;
using PluginBuilder.DataModels;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.PluginTests;

[Collection("Playwright Tests")]
public class ReleaseNotesUITests(ITestOutputHelper output) : PageTest
{
    private readonly XUnitLogger _log = new("ReleaseNotesUITests", output);

    [Fact]
    public async Task OwnerCanSaveReleaseNotesAndSwitchVersionsOnPublicPage()
    {
        await using var context = await CreateReleaseNotesContext();
        const string releaseNotes = "  Fixed duplicate payments.\nAdded retry support.  ";

        await context.Page.GetByRole(AriaRole.Button, new() { Name = "Edit changelog" }).ClickAsync();
        var modal = context.Page.Locator("#changelogModal");
        await Expect(modal).ToBeVisibleAsync();
        await modal.Locator("textarea[name='changelog']").FillAsync(releaseNotes);
        await modal.GetByRole(AriaRole.Button, new() { Name = "Save release note" }).ClickAsync();

        await Expect(context.Page).ToHaveURLAsync(new Regex(
            $"/plugins/{Regex.Escape(context.PluginSlug)}/builds/{context.FullBuildId.BuildId}$",
            RegexOptions.IgnoreCase));
        await Expect(context.Page.Locator(".alert-success")).ToContainTextAsync("Release notes saved");

        var expectedReleaseNotes = releaseNotes.Trim();
        var savedReleaseNotes = await context.Connection.QuerySingleOrDefaultAsync<string?>(
            "SELECT changelog FROM versions WHERE plugin_slug = @pluginSlug AND ver = @version",
            new { pluginSlug = context.PluginSlug, version = context.Version.VersionParts });
        Assert.NotNull(savedReleaseNotes);
        Assert.Equal(expectedReleaseNotes, savedReleaseNotes.Replace("\r\n", "\n"));

        var publishedVersion = await context.Tester.Server.CreateHttpClient()
            .GetPlugin(context.PluginSlug, context.Version.ToString());
        Assert.NotNull(publishedVersion);
        Assert.Equal(savedReleaseNotes, publishedVersion.Changelog);

        var previousVersion = PluginVersion.Parse("1.0.1.0");
        const string previousReleaseNotes = "Previous version release notes";
        Assert.True(await context.Connection.SetVersionBuild(
            context.FullBuildId,
            previousVersion,
            null,
            null,
            false));
        Assert.True(await context.Connection.UpdateVersionChangelog(
            context.PluginSlug,
            previousVersion,
            previousReleaseNotes));

        await context.Tester.GoToUrl($"/public/plugins/{context.PluginSlug}");
        var releaseNotesCard = context.Page.Locator("#plugin-changelog-card");
        await Expect(releaseNotesCard).ToBeVisibleAsync();
        await Expect(releaseNotesCard.GetByRole(AriaRole.Heading, new() { Name = "Release notes" })).ToBeVisibleAsync();
        await Expect(releaseNotesCard.Locator("#plugin-changelog-text")).ToHaveTextAsync(expectedReleaseNotes);

        await context.Page.Locator("#version-dropdown-btn").ClickAsync();
        await context.Page.Locator($"[data-version='{previousVersion}']").ClickAsync();
        await Expect(releaseNotesCard.Locator("#plugin-changelog-text")).ToHaveTextAsync(previousReleaseNotes);
    }

    [Fact]
    public async Task ReleaseNotesOverMaxLengthAreRejected()
    {
        await using var context = await CreateReleaseNotesContext();
        const string existingReleaseNotes = "Existing release notes";
        Assert.True(await context.Connection.UpdateVersionChangelog(context.PluginSlug, context.Version, existingReleaseNotes));

        var form = context.Page.Locator("#changelogModal form");
        var requestVerificationToken = await form
            .Locator("input[name='__RequestVerificationToken']")
            .InputValueAsync();
        var action = await form.GetAttributeAsync("action");
        Assert.NotNull(action);

        var formData = context.Page.Context.APIRequest.CreateFormData();
        formData.Set("__RequestVerificationToken", requestVerificationToken);
        formData.Set("changelog", new string('x', 4001));

        var response = await context.Page.Context.APIRequest.PostAsync(
            new Uri(new Uri(context.Page.Url), action).ToString(),
            new APIRequestContextOptions { Form = formData });

        Assert.True(response.Ok);
        Assert.Contains("4000 characters or fewer.", await response.TextAsync());

        var savedReleaseNotes = await context.Connection.QuerySingleOrDefaultAsync<string?>(
            "SELECT changelog FROM versions WHERE plugin_slug = @pluginSlug AND ver = @version",
            new { pluginSlug = context.PluginSlug, version = context.Version.VersionParts });
        Assert.Equal(existingReleaseNotes, savedReleaseNotes);
    }

    private async Task<ReleaseNotesContext> CreateReleaseNotesContext()
    {
        var tester = new PlaywrightTester(_log) { Server = { ReuseDatabase = false } };
        await tester.StartAsync();
        var connection = await tester.Server.GetService<DBConnectionFactory>().Open();

        var ownerEmail = $"release-notes-{Guid.NewGuid():N}@test.com";
        var ownerId = await tester.Server.CreateFakeUserAsync(ownerEmail, confirmEmail: true, githubVerified: true);
        var pluginSlug = "release-notes-" + PlaywrightTester.GetRandomUInt256()[..8];
        var fullBuildId = await tester.Server.CreateAndBuildPluginAsync(ownerId, pluginSlug);
        var manifestInfoJson = await connection.QuerySingleAsync<string>(
            "SELECT manifest_info FROM builds WHERE plugin_slug = @pluginSlug AND id = @buildId",
            new { pluginSlug, buildId = fullBuildId.BuildId });
        var manifest = PluginManifest.Parse(manifestInfoJson);

        await connection.SetVersionBuild(
            fullBuildId,
            manifest.Version,
            manifest.BTCPayMinVersion,
            manifest.BTCPayMaxVersion,
            false);
        await connection.SetPluginSettings(pluginSlug, new PluginSettings
        {
            PluginTitle = pluginSlug,
            Description = "Release notes test plugin",
            GitRepository = ServerTester.RepoUrl
        }, PluginVisibilityEnum.Listed);

        await tester.LogIn(ownerEmail);
        await tester.GoToUrl($"/plugins/{pluginSlug}/builds/{fullBuildId.BuildId}");
        var page = Assert.IsAssignableFrom<IPage>(tester.Page);

        return new ReleaseNotesContext(tester, connection, page, pluginSlug, fullBuildId, manifest.Version);
    }

    private sealed class ReleaseNotesContext(
        PlaywrightTester tester,
        NpgsqlConnection connection,
        IPage page,
        string pluginSlug,
        FullBuildId fullBuildId,
        PluginVersion version) : IAsyncDisposable
    {
        public PlaywrightTester Tester { get; } = tester;
        public NpgsqlConnection Connection { get; } = connection;
        public IPage Page { get; } = page;
        public string PluginSlug { get; } = pluginSlug;
        public FullBuildId FullBuildId { get; } = fullBuildId;
        public PluginVersion Version { get; } = version;

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            await Tester.DisposeAsync();
        }
    }
}
