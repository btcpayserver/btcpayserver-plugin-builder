using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using Npgsql;
using PluginBuilder.APIModels;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.DataModels;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using PluginBuilder.ViewModels.Admin;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class BuildWhitelistTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    private const string Password = "123456";

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task CreationAndRetry_RespectFlagAndWhitelist(bool enabled, bool whitelisted, bool api)
    {
        if (OperatingSystem.IsWindows())
            return;

        using var docker = new FakeDocker();
        await using var tester = Create("WhitelistMatrix");
        tester.ReuseDatabase = false;
        var git = new TestGitProvider();
        git.Release();
        tester.ConfigureServices = services => services.AddSingleton<IGitHostingProvider>(git);
        await tester.Start();
        docker.Activate();

        var email = NewEmail();
        var userId = await tester.CreateFakeUserAsync(email);
        var slug = NewSlug();
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.NewPlugin(slug, userId);
        var previousBuild = await conn.NewBuild(slug, new PluginBuildParameters(git.RepositoryUrl));
        await conn.UpdateBuild(new FullBuildId(slug, previousBuild), BuildStates.Failed, new JObject
        {
            ["error"] = "Previous build failed",
            ["gitRepository"] = git.RepositoryUrl,
            ["gitRef"] = "main"
        });

        using var browser = CreateBrowser(tester);
        await LogIn(browser, email);
        // Keep the same login session and form token while changing the setting.
        using var initialPage = await browser.GetAsync($"/plugins/{slug}/create");
        initialPage.EnsureSuccessStatusCode();
        var token = AntiforgeryToken(await initialPage.Content.ReadAsStringAsync());
        await SetAccess(tester, conn, enabled, whitelisted ? email : "");

        var allowed = enabled || whitelisted;
        foreach (var path in new[] { $"/plugins/{slug}/create", $"/plugins/{slug}/create?copyBuild={previousBuild}" })
        {
            using var get = await browser.GetAsync(path);
            Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable, get.StatusCode);
        }

        using var dashboard = await browser.GetAsync($"/plugins/{slug}");
        dashboard.EnsureSuccessStatusCode();
        var dashboardHtml = await dashboard.Content.ReadAsStringAsync();
        Assert.Equal(allowed, dashboardHtml.Contains("id=\"CreateNewBuild\"", StringComparison.Ordinal));
        Assert.Equal(allowed, dashboardHtml.Contains(">Retry</a>", StringComparison.Ordinal));
        using var details = await browser.GetAsync($"/plugins/{slug}/builds/{previousBuild}");
        details.EnsureSuccessStatusCode();
        Assert.Equal(allowed, (await details.Content.ReadAsStringAsync()).Contains(">Retry</a>", StringComparison.Ordinal));

        using var client = api ? CreateBrowser(tester).SetBasicAuth(email, Password) : null;
        using var content = api ? BuildJson(git.RepositoryUrl) : BuildForm(git.RepositoryUrl, token);
        using var response = await (client ?? browser).PostAsync(
            api ? $"/api/v1/plugins/{slug}/builds" : $"/plugins/{slug}/create?copyBuild={previousBuild}", content);

        Assert.Equal(allowed ? api ? HttpStatusCode.Created : HttpStatusCode.Redirect : HttpStatusCode.ServiceUnavailable,
            response.StatusCode);
        Assert.Equal(allowed ? 1 : 0, git.FetchCount);
        Assert.Equal(allowed ? 2 : 1, await BuildCount(conn, slug));
        if (allowed)
        {
            // The approved exception must pass every guard before container execution.
            await docker.WaitForCommand("container start ");
            await WaitForFailedBuild(conn, slug, previousBuild + 1);
            await docker.WaitForCommand("volume rm ");
        }
        else if (api)
        {
            Assert.Equal("builds-disabled", JObject.Parse(await response.Content.ReadAsStringAsync()).Value<string>("error"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingsEditor_ValidatesAccountsAndAppliesChangesToExistingSession(bool requireConfirmedEmail)
    {
        await using var tester = Create("WhitelistSettings");
        tester.ReuseDatabase = false;
        await tester.Start();
        var adminEmail = NewEmail();
        var adminId = await tester.CreateFakeUserAsync(adminEmail);
        var ownerEmail = NewEmail();
        var ownerId = await tester.CreateFakeUserAsync(ownerEmail);
        var unconfirmedEmail = NewEmail();
        var unconfirmedId = await tester.CreateFakeUserAsync(unconfirmedEmail, confirmEmail: false);
        var ambiguousEmail = NewEmail();
        await tester.CreateFakeUserAsync(ambiguousEmail);
        var duplicateId = await tester.CreateFakeUserAsync();
        var slug = NewSlug();
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.ExecuteAsync(
            """
            UPDATE "AspNetUsers" SET "Email" = @ambiguousEmail, "NormalizedEmail" = @normalizedEmail
            WHERE "Id" = @duplicateId
            """, new { ambiguousEmail, normalizedEmail = ambiguousEmail.ToUpperInvariant(), duplicateId });
        await MakeAdmin(conn, adminId);
        await conn.NewPlugin(slug, ownerId);
        var adminSlug = NewSlug();
        await conn.NewPlugin(adminSlug, adminId);
        await SetAccess(tester, conn, false, "");
        await conn.SettingsSetAsync(SettingsKeys.VerifiedEmailForLogin, requireConfirmedEmail ? "true" : "false");
        await tester.GetService<AdminSettingsCache>().RefreshIsVerifiedEmailRequiredForLogin(conn);

        using var admin = CreateBrowser(tester);
        using var owner = CreateBrowser(tester);
        await LogIn(admin, adminEmail);
        await LogIn(owner, ownerEmail);
        using var editor = await admin.GetAsync("/admin/SettingsEditor");
        editor.EnsureSuccessStatusCode();
        var editorHtml = await editor.Content.ReadAsStringAsync();
        Assert.Contains($"data-key=\"{SettingsKeys.NewBuildsWhitelist}\"", editorHtml, StringComparison.Ordinal);
        var token = AntiforgeryToken(editorHtml);
        var version = InputValue(editorHtml, "whitelistVersion");
        using (var blockedAdmin = await admin.GetAsync($"/plugins/{adminSlug}/create"))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, blockedAdmin.StatusCode);
        using (var blockedOwner = await owner.GetAsync($"/plugins/{slug}/create"))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, blockedOwner.StatusCode);

        using (var add = SettingForm($"  {ownerEmail.ToUpperInvariant()}, {ownerEmail}  ", token, version))
        using (var added = await admin.PostAsync("/admin/SettingsEditor", add))
            Assert.Equal(HttpStatusCode.Redirect, added.StatusCode);
        Assert.Equal(new[] { ownerId }, await WhitelistedIds(conn));
        using (var allowedOwner = await owner.GetAsync($"/plugins/{slug}/create"))
            Assert.Equal(HttpStatusCode.OK, allowedOwner.StatusCode);
        using (var stillBlockedAdmin = await admin.GetAsync($"/plugins/{adminSlug}/create"))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, stillBlockedAdmin.StatusCode);

        // Ordinary settings can still be saved without a whitelist version and preserve grants.
        using (var ordinaryForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["key"] = SettingsKeys.RegistrationEnabled,
            ["value"] = "false",
            ["__RequestVerificationToken"] = token
        }))
        using (var saved = await admin.PostAsync("/admin/SettingsEditor", ordinaryForm))
            Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.Equal("false", await conn.SettingsGetAsync(SettingsKeys.RegistrationEnabled));
        Assert.Equal(new[] { ownerId }, await WhitelistedIds(conn));

        version = (await GetEditor(admin)).Version;
        foreach (var invalid in new[] { NewEmail(), $"{ownerEmail}, {NewEmail()}", "not-an-email", ambiguousEmail })
        {
            using var invalidForm = SettingForm(invalid, token, version);
            using var rejected = await admin.PostAsync("/admin/SettingsEditor", invalidForm);
            Assert.Equal(HttpStatusCode.Redirect, rejected.StatusCode);
            using var rejectionPage = await admin.GetAsync("/admin/SettingsEditor");
            rejectionPage.EnsureSuccessStatusCode();
            Assert.Contains("Whitelist not saved", await rejectionPage.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Equal(new[] { ownerId }, await WhitelistedIds(conn));
            using var stillAllowed = await owner.GetAsync($"/plugins/{slug}/create");
            Assert.Equal(HttpStatusCode.OK, stillAllowed.StatusCode);
        }

        using (var addUnconfirmed = SettingForm($"{ownerEmail}, {unconfirmedEmail}", token, version))
        using (var result = await admin.PostAsync("/admin/SettingsEditor", addUnconfirmed))
            Assert.Equal(HttpStatusCode.Redirect, result.StatusCode);
        using (var resultPage = await admin.GetAsync("/admin/SettingsEditor"))
        {
            resultPage.EnsureSuccessStatusCode();
            Assert.Equal(requireConfirmedEmail,
                (await resultPage.Content.ReadAsStringAsync()).Contains("Whitelist not saved", StringComparison.Ordinal));
        }
        Assert.Equal((requireConfirmedEmail ? new[] { ownerId } : new[] { ownerId, unconfirmedId }).Order(),
            await WhitelistedIds(conn));

        version = (await GetEditor(admin)).Version;
        using (var remove = SettingForm("", token, version))
        using (var removed = await admin.PostAsync("/admin/SettingsEditor", remove))
            Assert.Equal(HttpStatusCode.Redirect, removed.StatusCode);
        using var revoked = await owner.GetAsync($"/plugins/{slug}/create");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, revoked.StatusCode);
        Assert.Empty(await WhitelistedIds(conn));
        Assert.Null(await conn.SettingsGetAsync(SettingsKeys.NewBuildsWhitelist));
    }

    [Fact]
    public async Task SettingsEditor_RejectsStaleOrMissingSaveVersionAndMissingDeleteVersion()
    {
        await using var tester = Create("WhitelistStaleSettings");
        tester.ReuseDatabase = false;
        await tester.Start();
        var firstAdminEmail = NewEmail();
        var firstAdminId = await tester.CreateFakeUserAsync(firstAdminEmail);
        var secondAdminEmail = NewEmail();
        var secondAdminId = await tester.CreateFakeUserAsync(secondAdminEmail);
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await MakeAdmin(conn, firstAdminId);
        await MakeAdmin(conn, secondAdminId);
        using var firstAdmin = CreateBrowser(tester);
        using var secondAdmin = CreateBrowser(tester);
        await LogIn(firstAdmin, firstAdminEmail);
        await LogIn(secondAdmin, secondAdminEmail);

        var firstPage = await GetEditor(firstAdmin);
        var secondPage = await GetEditor(secondAdmin);
        Assert.Equal(firstPage.Version, secondPage.Version);
        using (var form = SettingForm(firstAdminEmail, firstPage.Token, firstPage.Version))
        using (var saved = await firstAdmin.PostAsync("/admin/SettingsEditor", form))
            Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);

        const string conflict = "The build whitelist has changed. Reload the page before saving or deleting it.";
        foreach (var invalidVersion in new[] { secondPage.Version, null })
        {
            using var staleForm = SettingForm(secondAdminEmail, secondPage.Token, invalidVersion);
            using var rejected = await secondAdmin.PostAsync("/admin/SettingsEditor", staleForm);
            Assert.Equal(HttpStatusCode.Redirect, rejected.StatusCode);
            Assert.Contains(conflict, (await GetEditor(secondAdmin)).Html, StringComparison.Ordinal);
            Assert.Equal(new[] { firstAdminId }, await WhitelistedIds(conn));
        }

        using var delete = await DeleteWhitelist(secondAdmin, secondPage.Token, version: null);
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Equal(conflict, await delete.Content.ReadAsStringAsync());
        Assert.Equal(new[] { firstAdminId }, await WhitelistedIds(conn));
    }

    [Fact]
    public async Task SettingsEditor_ConcurrentSavesCompareVersionAfterAcquiringLock()
    {
        await using var tester = Create("WhitelistConcurrentSettings");
        tester.ReuseDatabase = false;
        await tester.Start();
        var firstEmail = NewEmail();
        var firstId = await tester.CreateFakeUserAsync(firstEmail);
        var secondEmail = NewEmail();
        var secondId = await tester.CreateFakeUserAsync(secondEmail);
        await using var blocking = await tester.GetService<DBConnectionFactory>().Open();
        await using var observer = await tester.GetService<DBConnectionFactory>().Open();
        await MakeAdmin(blocking, firstId);
        await MakeAdmin(blocking, secondId);
        using var firstAdmin = CreateBrowser(tester);
        using var secondAdmin = CreateBrowser(tester);
        await LogIn(firstAdmin, firstEmail);
        await LogIn(secondAdmin, secondEmail);
        var firstPage = await GetEditor(firstAdmin);
        var secondPage = await GetEditor(secondAdmin);
        Assert.Equal(firstPage.Version, secondPage.Version);
        Assert.Empty(await WhitelistedIds(observer));

        await using var blocker = await blocking.BeginTransactionAsync();
        await blocking.ExecuteAsync("LOCK TABLE build_whitelist IN SHARE ROW EXCLUSIVE MODE");
        using var firstForm = SettingForm(firstEmail, firstPage.Token, firstPage.Version);
        using var secondForm = SettingForm(secondEmail, secondPage.Token, secondPage.Version);
        using var requestsTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pending = new[]
        {
            firstAdmin.PostAsync("/admin/SettingsEditor", firstForm, requestsTimeout.Token),
            secondAdmin.PostAsync("/admin/SettingsEditor", secondForm, requestsTimeout.Token)
        };
        try
        {
            using var waitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            // Establish that both real requests reached the contested lock before either can save.
            while (await observer.ExecuteScalarAsync<int>(new CommandDefinition(
                       """
                       SELECT COUNT(*) FROM pg_stat_activity
                       WHERE datname = current_database()
                           AND @blockingPid = ANY(pg_blocking_pids(pid))
                       """, new { blockingPid = blocking.ProcessID }, cancellationToken: waitTimeout.Token)) < 2)
            {
                Assert.All(pending, request => Assert.False(request.IsCompleted,
                    "Both saves must wait for the transaction holding the whitelist lock."));
                await Task.Delay(10, waitTimeout.Token);
            }
            await blocker.CommitAsync();

            foreach (var request in pending)
                Assert.Equal(HttpStatusCode.Redirect, (await request.WaitAsync(TimeSpan.FromSeconds(10))).StatusCode);
            const string conflict = "The build whitelist has changed. Reload the page before saving or deleting it.";
            var firstConflict = (await GetEditor(firstAdmin)).Html.Contains(conflict, StringComparison.Ordinal);
            var secondConflict = (await GetEditor(secondAdmin)).Html.Contains(conflict, StringComparison.Ordinal);
            Assert.NotEqual(firstConflict, secondConflict);
            Assert.Equal(new[] { firstConflict ? secondId : firstId }, await WhitelistedIds(observer));
        }
        finally
        {
            await blocker.DisposeAsync();
            foreach (var request in pending)
                try
                {
                    (await request.WaitAsync(TimeSpan.FromSeconds(10))).Dispose();
                }
                catch
                {
                    // Preserve the original failure while releasing both blocked HTTP requests.
                }
        }
    }

    [Fact]
    public async Task SettingsEditor_VersionDetectsDifferentAccountWithTheSameEmail()
    {
        await using var tester = Create("WhitelistIdSnapshot");
        tester.ReuseDatabase = false;
        await tester.Start();
        var adminEmail = NewEmail();
        var adminId = await tester.CreateFakeUserAsync(adminEmail);
        var email = NewEmail();
        var originalId = await tester.CreateFakeUserAsync(email);
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await MakeAdmin(conn, adminId);
        await SetAccess(tester, conn, false, email);
        using var admin = CreateBrowser(tester);
        await LogIn(admin, adminEmail);
        var before = await GetEditor(admin);

        await conn.ExecuteAsync("""DELETE FROM "AspNetUsers" WHERE "Id" = @originalId""", new { originalId });
        var replacementId = await tester.CreateFakeUserAsync(email);
        Assert.NotEqual(originalId, replacementId);
        Assert.Empty(await WhitelistedIds(conn));
        await SetAccess(tester, conn, false, email);
        var after = await GetEditor(admin);
        Assert.Equal(WhitelistValue(before.Html), WhitelistValue(after.Html));
        Assert.NotEqual(before.Version, after.Version);

        using var deleted = await DeleteWhitelist(admin, before.Token, before.Version);
        Assert.Equal(HttpStatusCode.Conflict, deleted.StatusCode);
        Assert.Equal(new[] { replacementId }, await WhitelistedIds(conn));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnconfirmedWhitelistedAccount_TracksLoginVerificationFlagInExistingSession(bool api)
    {
        if (OperatingSystem.IsWindows())
            return;

        using var docker = new FakeDocker();
        await using var tester = Create("WhitelistEmailFlag");
        tester.ReuseDatabase = false;
        var git = new TestGitProvider();
        git.Release();
        tester.ConfigureServices = services => services.AddSingleton<IGitHostingProvider>(git);
        await tester.Start();
        docker.Activate();
        var email = NewEmail();
        var userId = await tester.CreateFakeUserAsync(email, confirmEmail: false);
        var slug = NewSlug();
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.NewPlugin(slug, userId);
        // Configured SMTP ensures publishing checks cannot silently skip verification.
        await tester.GetService<EmailService>().SaveEmailSettingsToDatabase(new EmailSettingsViewModel
        {
            Server = "127.0.0.1",
            Port = 1,
            Username = "whitelist-test",
            Password = "whitelist-test",
            From = "Whitelist Test <whitelist@example.test>"
        });
        await conn.SettingsSetAsync(SettingsKeys.VerifiedEmailForLogin, "false");
        await conn.SettingsSetAsync(SettingsKeys.VerifiedEmailForPluginPublish, "false");
        await tester.GetService<AdminSettingsCache>().RefreshAllVerifiedEmailSettings(conn);
        await SetAccess(tester, conn, false, email);

        using var browser = CreateBrowser(tester);
        await LogIn(browser, email);
        using var initialPage = await browser.GetAsync($"/plugins/{slug}/create");
        initialPage.EnsureSuccessStatusCode();
        var token = AntiforgeryToken(await initialPage.Content.ReadAsStringAsync());
        using var client = api ? CreateBrowser(tester).SetBasicAuth(email, Password) : null;
        var accepted = 0;
        foreach (var requireConfirmedEmail in new[] { false, true, false })
        {
            await conn.SettingsSetAsync(SettingsKeys.VerifiedEmailForLogin, requireConfirmedEmail ? "true" : "false");
            await tester.GetService<AdminSettingsCache>().RefreshIsVerifiedEmailRequiredForLogin(conn);
            using (var page = await browser.GetAsync($"/plugins/{slug}/create"))
                Assert.Equal(requireConfirmedEmail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK, page.StatusCode);
            using (var dashboard = await browser.GetAsync($"/plugins/{slug}"))
            {
                dashboard.EnsureSuccessStatusCode();
                Assert.Equal(!requireConfirmedEmail,
                    (await dashboard.Content.ReadAsStringAsync()).Contains("id=\"CreateNewBuild\"", StringComparison.Ordinal));
            }
            using var content = api ? BuildJson(git.RepositoryUrl) : BuildForm(git.RepositoryUrl, token);
            using var response = await (client ?? browser).PostAsync(
                api ? $"/api/v1/plugins/{slug}/builds" : $"/plugins/{slug}/create", content);
            Assert.Equal(requireConfirmedEmail ? HttpStatusCode.ServiceUnavailable :
                api ? HttpStatusCode.Created : HttpStatusCode.Redirect, response.StatusCode);
            if (!requireConfirmedEmail)
            {
                await docker.WaitForCommand("container start ", ++accepted);
                await WaitForFailedBuild(conn, slug, accepted - 1);
                await docker.WaitForCommand("volume rm ", accepted);
            }
            Assert.Equal(accepted, await BuildCount(conn, slug));
            Assert.Equal(accepted, git.FetchCount);
        }

        // The separate publishing requirement still applies when login verification is disabled.
        await conn.SettingsSetAsync(SettingsKeys.VerifiedEmailForPluginPublish, "true");
        await tester.GetService<AdminSettingsCache>().RefreshIsVerifiedEmailRequiredForPublish(conn);
        using var blockedContent = api ? BuildJson(git.RepositoryUrl) : BuildForm(git.RepositoryUrl, token);
        using var blocked = await (client ?? browser).PostAsync(
            api ? $"/api/v1/plugins/{slug}/builds" : $"/plugins/{slug}/create", blockedContent);
        Assert.Equal(api ? HttpStatusCode.Forbidden : HttpStatusCode.Redirect, blocked.StatusCode);
        if (!api)
            Assert.Contains("account", blocked.Headers.Location?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(accepted, await BuildCount(conn, slug));
        Assert.Equal(accepted, git.FetchCount);
    }

    [Fact]
    public async Task OrdinaryUser_CannotManageWhitelistOrBuildAnotherOwnersPlugin()
    {
        await using var tester = Create("WhitelistPermissions");
        tester.ReuseDatabase = false;
        var git = new TestGitProvider();
        git.Release();
        tester.ConfigureServices = services => services.AddSingleton<IGitHostingProvider>(git);
        await tester.Start();
        var email = NewEmail();
        var userId = await tester.CreateFakeUserAsync(email);
        var otherId = await tester.CreateFakeUserAsync();
        var ownSlug = NewSlug();
        var otherSlug = NewSlug();
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.NewPlugin(ownSlug, userId);
        await conn.NewPlugin(otherSlug, otherId);
        await SetAccess(tester, conn, false, email);
        using var browser = CreateBrowser(tester);
        await LogIn(browser, email);
        using var ownPage = await browser.GetAsync($"/plugins/{ownSlug}/create");
        ownPage.EnsureSuccessStatusCode();
        var token = AntiforgeryToken(await ownPage.Content.ReadAsStringAsync());

        using (var unauthorizedEditor = await browser.GetAsync("/admin/SettingsEditor"))
            AssertDenied(unauthorizedEditor);
        using (var unauthorizedForm = SettingForm("", token))
        using (var unauthorizedPost = await browser.PostAsync("/admin/SettingsEditor", unauthorizedForm))
            AssertDenied(unauthorizedPost);
        using (var unauthorizedDelete = new HttpRequestMessage(HttpMethod.Delete, "/admin/SettingsEditor"))
        {
            unauthorizedDelete.Content = SettingForm("", token);
            using var deleted = await browser.SendAsync(unauthorizedDelete);
            AssertDenied(deleted);
        }
        Assert.Equal(new[] { userId }, await WhitelistedIds(conn));

        using (var otherPage = await browser.GetAsync($"/plugins/{otherSlug}/create"))
            AssertDenied(otherPage);
        using (var otherForm = BuildForm(git.RepositoryUrl, token))
        using (var otherPost = await browser.PostAsync($"/plugins/{otherSlug}/create", otherForm))
            AssertDenied(otherPost);
        using var api = CreateBrowser(tester).SetBasicAuth(email, Password);
        using var content = BuildJson(git.RepositoryUrl);
        using var deniedApi = await api.PostAsync($"/api/v1/plugins/{otherSlug}/builds", content);
        Assert.Equal(HttpStatusCode.Forbidden, deniedApi.StatusCode);
        Assert.Equal(0, git.FetchCount);
        Assert.Equal(0, await BuildCount(conn, otherSlug));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovalDuringRepositoryLookup_RejectsBeforeAcceptingBuild(bool api)
    {
        await using var tester = Create("WhitelistLookupRace");
        tester.ReuseDatabase = false;
        var git = new TestGitProvider();
        tester.ConfigureServices = services => services.AddSingleton<IGitHostingProvider>(git);
        await tester.Start();
        var email = NewEmail();
        var userId = await tester.CreateFakeUserAsync(email);
        var slug = NewSlug();
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.NewPlugin(slug, userId);
        await SetAccess(tester, conn, false, email);
        using var client = CreateBrowser(tester);
        string token = "";
        if (api)
            client.SetBasicAuth(email, Password);
        else
        {
            await LogIn(client, email);
            using var page = await client.GetAsync($"/plugins/{slug}/create");
            page.EnsureSuccessStatusCode();
            token = AntiforgeryToken(await page.Content.ReadAsStringAsync());
        }
        using var content = api ? BuildJson(git.RepositoryUrl) : BuildForm(git.RepositoryUrl, token);
        var request = client.PostAsync(api ? $"/api/v1/plugins/{slug}/builds" : $"/plugins/{slug}/create", content);
        try
        {
            await git.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            await SetAccess(tester, conn, false, "");
        }
        finally
        {
            git.Release();
        }
        using var response = await request.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, git.FetchCount);
        Assert.Equal(0, await BuildCount(conn, slug));
        Assert.False(await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM builds_ids WHERE plugin_slug = @slug)", new { slug = slug.ToString() }));
    }

    [Fact]
    public async Task Whitelist_DoesNotBypassGithubVerification()
    {
        await using var tester = Create("WhitelistVerification");
        tester.ReuseDatabase = false;
        var git = new TestGitProvider();
        git.Release();
        tester.ConfigureServices = services => services.AddSingleton<IGitHostingProvider>(git);
        await tester.Start();
        var email = NewEmail();
        var userId = await tester.CreateFakeUserAsync(email, githubVerified: false);
        var slug = NewSlug();
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.NewPlugin(slug, userId);
        await SetAccess(tester, conn, false, email);
        await conn.SettingsSetAsync(SettingsKeys.VerifiedGithub, "true");
        await tester.GetService<AdminSettingsCache>().RefreshAllAdminSettings(conn);

        using var browser = CreateBrowser(tester);
        await LogIn(browser, email);
        using var get = await browser.GetAsync($"/plugins/{slug}/create");
        Assert.Equal(HttpStatusCode.Redirect, get.StatusCode);
        Assert.Contains("account", get.Headers.Location?.ToString(), StringComparison.OrdinalIgnoreCase);
        using var api = CreateBrowser(tester).SetBasicAuth(email, Password);
        using var body = BuildJson(git.RepositoryUrl);
        using var response = await api.PostAsync($"/api/v1/plugins/{slug}/builds", body);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, git.FetchCount);
        Assert.Equal(0, await BuildCount(conn, slug));
    }

    [Fact]
    public async Task RemovingUser_DoesNotCancelPreviouslyAcceptedQueuedBuild()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var docker = new FakeDocker();
        docker.BlockVolumes();
        await using var tester = Create("WhitelistAcceptedQueue");
        tester.ReuseDatabase = false;
        await tester.Start();
        docker.Activate();
        var email = NewEmail();
        var userId = await tester.CreateFakeUserAsync(email);
        var slug = NewSlug();
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.NewPlugin(slug, userId);
        await SetAccess(tester, conn, false, email);
        List<FullBuildId> builds = [];
        for (var i = 0; i < 6; i++)
            builds.Add(new FullBuildId(slug, await conn.NewBuild(slug, new PluginBuildParameters("https://example.invalid/repository.git"))));
        var service = tester.GetService<BuildService>();
        List<Task> pending = [];
        try
        {
            pending.AddRange(builds.Take(5).Select(id => service.Build(id, true)));
            await docker.WaitForCommand("volume create ", 5);
            var queued = service.Build(builds[^1], true);
            pending.Add(queued);
            Assert.False(queued.IsCompleted);

            await SetAccess(tester, conn, false, "");
            docker.ReleaseVolumes();
            foreach (var task in pending)
                await Assert.ThrowsAsync<BuildServiceException>(() => task.WaitAsync(TimeSpan.FromSeconds(10)));
            var last = builds[^1];
            var error = await conn.ExecuteScalarAsync<string>(
                "SELECT build_info->>'error' FROM builds WHERE plugin_slug = @slug AND id = @id",
                new { slug = slug.ToString(), id = last.BuildId });
            Assert.Equal("docker build failed", error);
            Assert.Equal(builds.Count, (await docker.Commands()).Count(command =>
                command.StartsWith("container start ", StringComparison.Ordinal)));
        }
        finally
        {
            docker.ReleaseVolumes();
            foreach (var task in pending)
                try
                {
                    await task.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch
                {
                    // Fake container execution intentionally fails; release every execution slot.
                }
        }
    }

    private static string NewEmail() => $"whitelist-{Guid.NewGuid():N}@example.com";
    private static PluginSlug NewSlug() => new("whitelist-" + Guid.NewGuid().ToString("N")[..8]);

    private static async Task SetAccess(ServerTester tester, NpgsqlConnection conn, bool enabled, string emails)
    {
        using var scope = tester.WebApp.Services.CreateScope();
        var normalizer = scope.ServiceProvider.GetRequiredService<ILookupNormalizer>();
        await conn.SettingsSetAsync(SettingsKeys.NewBuildsEnabled, enabled ? "true" : "false");
        await conn.ExecuteAsync("DELETE FROM build_whitelist");
        foreach (var email in emails.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            await conn.ExecuteAsync(
                """
                INSERT INTO build_whitelist (user_id)
                SELECT "Id" FROM "AspNetUsers" WHERE "NormalizedEmail" = @normalizedEmail
                """, new { normalizedEmail = normalizer.NormalizeEmail(email) });
        await tester.GetService<AdminSettingsCache>().RefreshFeatureSettings(conn);
    }

    private static Task MakeAdmin(NpgsqlConnection conn, string userId) => conn.ExecuteAsync(
        """
        INSERT INTO "AspNetUserRoles" ("UserId", "RoleId")
        SELECT @userId, "Id" FROM "AspNetRoles" WHERE "NormalizedName" = 'SERVERADMIN'
        """, new { userId });

    private static Task<int> BuildCount(NpgsqlConnection conn, PluginSlug slug) => conn.ExecuteScalarAsync<int>(
        "SELECT COUNT(*) FROM builds WHERE plugin_slug = @slug", new { slug = slug.ToString() });

    private static async Task WaitForFailedBuild(NpgsqlConnection conn, PluginSlug slug, long buildId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await conn.ExecuteScalarAsync<string>(
                   "SELECT state FROM builds WHERE plugin_slug = @slug AND id = @buildId", new { slug = slug.ToString(), buildId })
               != BuildStates.Failed.ToEventName())
            await Task.Delay(10, timeout.Token);
    }

    private static HttpClient CreateBrowser(ServerTester tester) => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        CookieContainer = new CookieContainer()
    }) { BaseAddress = new Uri(tester.WebApp.Urls.First()) };

    private static async Task LogIn(HttpClient client, string email)
    {
        using var page = await client.GetAsync("/login");
        page.EnsureSuccessStatusCode();
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = Password,
            ["__RequestVerificationToken"] = AntiforgeryToken(await page.Content.ReadAsStringAsync())
        });
        using var response = await client.PostAsync("/login", form);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static string AntiforgeryToken(string html) => InputValue(html, "__RequestVerificationToken");

    private static string InputValue(string html, string name)
    {
        var input = Regex.Match(html, $"<input\\b(?=[^>]*\\bname=\"{Regex.Escape(name)}\")[^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        Assert.True(input.Success, $"Page must contain the {name} input.");
        var value = Regex.Match(input.Value, "\\bvalue=\"([^\"]+)\"");
        Assert.True(value.Success);
        return WebUtility.HtmlDecode(value.Groups[1].Value);
    }

    private static HttpContent BuildJson(string repository) => new StringContent(new JObject
    {
        ["gitRepository"] = repository,
        ["gitRef"] = "main",
        ["buildConfig"] = ServerTester.BuildCfg
    }.ToString(), Encoding.UTF8, "application/json");

    private static HttpContent BuildForm(string repository, string token) => new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["GitRepository"] = repository,
        ["GitRef"] = "main",
        ["BuildConfig"] = ServerTester.BuildCfg,
        ["__RequestVerificationToken"] = token
    });

    private static HttpContent SettingForm(string value, string token, string? version = null)
    {
        var values = new Dictionary<string, string>
        {
            ["key"] = SettingsKeys.NewBuildsWhitelist,
            ["value"] = value,
            ["__RequestVerificationToken"] = token
        };
        if (version is not null)
            values["whitelistVersion"] = version;
        return new FormUrlEncodedContent(values);
    }

    private static async Task<(string Token, string Version, string Html)> GetEditor(HttpClient browser)
    {
        using var response = await browser.GetAsync("/admin/SettingsEditor");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        return (AntiforgeryToken(html), InputValue(html, "whitelistVersion"), html);
    }

    private static async Task<string[]> WhitelistedIds(NpgsqlConnection conn) =>
        (await conn.QueryAsync<string>("SELECT user_id FROM build_whitelist ORDER BY user_id")).ToArray();

    private static string WhitelistValue(string html)
    {
        var row = Regex.Match(html, $"<tr data-key=\"{SettingsKeys.NewBuildsWhitelist}\">(.*?)</tr>", RegexOptions.Singleline);
        Assert.True(row.Success, "The virtual whitelist row must always be visible.");
        var value = Regex.Match(row.Value, "<td class=\"value\">(.*?)</td>", RegexOptions.Singleline);
        Assert.True(value.Success);
        return WebUtility.HtmlDecode(value.Groups[1].Value).Trim();
    }

    private static async Task<HttpResponseMessage> DeleteWhitelist(HttpClient browser, string token, string? version)
    {
        var url = $"/admin/SettingsEditor?key={SettingsKeys.NewBuildsWhitelist}";
        if (version is not null)
            url += "&whitelistVersion=" + Uri.EscapeDataString(version);
        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        request.Headers.Add("RequestVerificationToken", token);
        return await browser.SendAsync(request);
    }

    private static void AssertDenied(HttpResponseMessage response)
    {
        // Cookie authentication redirects access-denied requests; Basic authentication returns 403.
        Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Redirect);
        if (response.StatusCode == HttpStatusCode.Redirect)
            Assert.Contains("/errors/403", response.Headers.Location?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TestGitProvider : IGitHostingProvider
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _fetchCount;
        public string RepositoryUrl { get; } = $"https://whitelist-{Guid.NewGuid():N}.invalid/repository.git";
        public int FetchCount => Volatile.Read(ref _fetchCount);
        public Task Entered => _entered.Task;
        public bool CanHandle(string repoUrl) => repoUrl == RepositoryUrl;
        public async Task<string> FetchIdentifierFromCsprojAsync(string repoUrl, string gitRef, string? pluginDir = null)
        {
            Interlocked.Increment(ref _fetchCount);
            _entered.TrySetResult();
            return await _release.Task;
        }
        public void Release() => _release.TrySetResult("Whitelist.TestPlugin");
        public Task<List<GitHubContributor>> GetContributorsAsync(string repoUrl, string pluginDir) => Task.FromResult(new List<GitHubContributor>());
        public string? GetSourceUrl(string repoUrl, string? commit, string? pluginDir) => null;
        public (string Owner, string RepoName)? ParseRepository(string repoUrl) => null;
    }

    private sealed class FakeDocker : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"plugin-builder-whitelist-{Guid.NewGuid():N}");
        private readonly string? _originalPath = Environment.GetEnvironmentVariable("PATH");
        private readonly string? _originalSkipBuild = Environment.GetEnvironmentVariable("DOCKER_STARTUP_SKIP_BUILD");
        public FakeDocker()
        {
            if (OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("The fake Docker executable uses a POSIX shell.");
            Directory.CreateDirectory(_directory);
            var executable = Path.Combine(_directory, "docker");
            File.WriteAllText(executable, """
                #!/bin/sh
                set -eu
                state="$(dirname "$0")"
                printf '%s\n' "$*" >> "$state/commands"
                case "$1:$2" in
                    volume:create)
                        for argument in "$@"; do volume="$argument"; done
                        while [ -f "$state/block-volumes" ] && [ ! -f "$state/release-volumes" ]; do sleep 0.01; done
                        printf '%s\n' "$volume"
                        ;;
                    container:create) printf '%s\n' fake-container-id ;;
                    container:start) exit 41 ;;
                    volume:rm|container:rm) ;;
                    *) exit 2 ;;
                esac
                """);
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Environment.SetEnvironmentVariable("DOCKER_STARTUP_SKIP_BUILD", "true");
        }
        public void Activate() => Environment.SetEnvironmentVariable("PATH", _directory + Path.PathSeparator + _originalPath);
        public void BlockVolumes() => File.WriteAllText(Path.Combine(_directory, "block-volumes"), "");
        public void ReleaseVolumes() => File.WriteAllText(Path.Combine(_directory, "release-volumes"), "");
        public Task<string[]> Commands() => File.ReadAllLinesAsync(Path.Combine(_directory, "commands"));
        public async Task WaitForCommand(string prefix, int count = 1)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var path = Path.Combine(_directory, "commands");
            while (!File.Exists(path) || (await File.ReadAllLinesAsync(path, timeout.Token)).Count(line => line.StartsWith(prefix, StringComparison.Ordinal)) < count)
                await Task.Delay(10, timeout.Token);
        }
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("PATH", _originalPath);
            Environment.SetEnvironmentVariable("DOCKER_STARTUP_SKIP_BUILD", _originalSkipBuild);
            Directory.Delete(_directory, recursive: true);
        }
    }
}
