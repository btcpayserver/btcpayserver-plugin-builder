using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json.Linq;
using PluginBuilder.APIModels;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.DataModels;
using PluginBuilder.Events;
using PluginBuilder.HostedServices;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

using PluginBuilder.Builds.Services;

namespace PluginBuilder.Tests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class FeatureFlagTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    [Fact]
    public async Task FeatureFlags_AreSeededEnabledButFailClosedWhenMissingOrInvalid()
    {
        await using var tester = Create("FeatureFlagsDefaults");
        tester.ReuseDatabase = false;
        await tester.Start();

        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        Assert.Equal("true", await conn.SettingsGetAsync(SettingsKeys.RegistrationEnabled));
        Assert.Equal("true", await conn.SettingsGetAsync(SettingsKeys.NewBuildsEnabled));

        var settings = tester.GetService<AdminSettingsCache>();
        Assert.True(settings.RegistrationEnabled);
        Assert.True(settings.NewBuildsEnabled);

        await conn.SettingsDeleteAsync(SettingsKeys.RegistrationEnabled);
        await conn.SettingsDeleteAsync(SettingsKeys.NewBuildsEnabled);
        await conn.SettingsInitialize();
        await settings.RefreshFeatureSettings(conn);

        Assert.Null(await conn.SettingsGetAsync(SettingsKeys.RegistrationEnabled));
        Assert.Null(await conn.SettingsGetAsync(SettingsKeys.NewBuildsEnabled));
        Assert.False(settings.RegistrationEnabled);
        Assert.False(settings.NewBuildsEnabled);

        await conn.SettingsSetAsync(SettingsKeys.RegistrationEnabled, "not-a-boolean");
        await conn.SettingsSetAsync(SettingsKeys.NewBuildsEnabled, "not-a-boolean");
        await settings.RefreshFeatureSettings(conn);

        Assert.Equal("not-a-boolean", await conn.SettingsGetAsync(SettingsKeys.RegistrationEnabled));
        Assert.Equal("not-a-boolean", await conn.SettingsGetAsync(SettingsKeys.NewBuildsEnabled));
        Assert.False(settings.RegistrationEnabled);
        Assert.False(settings.NewBuildsEnabled);
    }

    [Fact]
    public async Task RegistrationDisabled_BlocksGetAndPostBeforeCreatingUser()
    {
        await using var tester = Create("RegistrationFlag");
        tester.ReuseDatabase = false;
        await tester.Start();

        using var client = CreateBrowserClient(tester);

        // Fetch a valid antiforgery token while registration is still enabled. This
        // ensures the POST reaches the action and is rejected by the feature flag.
        using var registrationPage = await client.GetAsync("/register");
        registrationPage.EnsureSuccessStatusCode();
        var antiforgeryToken = ExtractAntiforgeryToken(await registrationPage.Content.ReadAsStringAsync());

        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await DisableFeature(tester, conn, SettingsKeys.RegistrationEnabled);

        using var getResponse = await client.GetAsync("/register");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, getResponse.StatusCode);

        using var loginResponse = await client.GetAsync("/login");
        loginResponse.EnsureSuccessStatusCode();
        Assert.DoesNotContain("id=\"Register\"", await loginResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var email = $"registration-disabled-{Guid.NewGuid():N}@example.com";
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = "123456",
            ["ConfirmPassword"] = "123456",
            ["__RequestVerificationToken"] = antiforgeryToken
        });
        using var postResponse = await client.PostAsync("/register", form);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, postResponse.StatusCode);

        Assert.False(await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM \"AspNetUsers\" WHERE \"Email\" = @email)",
            new { email }));
    }

    [Fact]
    public async Task NewBuildsDisabled_BlocksApiBeforeValidationOrBuildCreation()
    {
        const string password = "123456";

        await using var tester = Create("BuildsApiFlag");
        tester.ReuseDatabase = false;
        var gitProvider = new BlockingGitHostingProvider();
        tester.ConfigureServices = services =>
        {
            services.RemoveAll<IGitHostingProvider>();
            services.AddSingleton<IGitHostingProvider>(gitProvider);
        };
        await tester.Start();

        var email = $"build-disabled-{Guid.NewGuid():N}@example.com";
        var ownerId = await tester.CreateFakeUserAsync(email, password);
        var pluginSlug = "build-disabled-" + Guid.NewGuid().ToString("N")[..8];

        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.NewPlugin(pluginSlug, ownerId);
        await DisableFeature(tester, conn, SettingsKeys.NewBuildsEnabled);
        gitProvider.Release("FeatureFlags.Api");

        using var client = tester.CreateHttpClient().SetBasicAuth(email, password);
        using var content = CreateBuildJson(gitProvider.RepositoryUrl);
        using var response = await client.PostAsync($"/api/v1/plugins/{pluginSlug}/builds", content);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, gitProvider.FetchCount);
        var result = JObject.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("builds-disabled", result.Value<string>("error"));
        Assert.False(await HasBuildRows(conn, pluginSlug));
    }

    [Fact]
    public async Task NewBuildsDisabled_BlocksUiGetAndPostAndHidesCreateActions()
    {
        const string password = "123456";

        await using var tester = Create("BuildsUiFlag");
        tester.ReuseDatabase = false;
        var gitProvider = new BlockingGitHostingProvider();
        tester.ConfigureServices = services =>
        {
            services.RemoveAll<IGitHostingProvider>();
            services.AddSingleton<IGitHostingProvider>(gitProvider);
        };
        await tester.Start();

        var email = $"build-ui-disabled-{Guid.NewGuid():N}@example.com";
        var ownerId = await tester.CreateFakeUserAsync(email, password);
        var pluginSlug = new PluginSlug("build-ui-disabled-" + Guid.NewGuid().ToString("N")[..8]);

        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.NewPlugin(pluginSlug, ownerId);

        using var client = CreateBrowserClient(tester);
        await LogIn(client, email, password);

        var createUrl = $"/plugins/{pluginSlug}/create";
        using var createPage = await client.GetAsync(createUrl);
        createPage.EnsureSuccessStatusCode();
        var antiforgeryToken = ExtractAntiforgeryToken(await createPage.Content.ReadAsStringAsync());

        await DisableFeature(tester, conn, SettingsKeys.NewBuildsEnabled);
        gitProvider.Release("FeatureFlags.Ui");

        using var getResponse = await client.GetAsync(createUrl);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, getResponse.StatusCode);

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["GitRepository"] = gitProvider.RepositoryUrl,
            ["GitRef"] = "main",
            ["BuildConfig"] = ServerTester.BuildCfg,
            ["__RequestVerificationToken"] = antiforgeryToken
        });
        using var postResponse = await client.PostAsync(createUrl, form);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, postResponse.StatusCode);
        Assert.Equal(0, gitProvider.FetchCount);
        Assert.False(await HasBuildRows(conn, pluginSlug));

        var existingBuildId = await conn.NewBuild(
            pluginSlug,
            new PluginBuildParameters("https://example.invalid/existing.git"));
        await conn.UpdateBuild(
            new FullBuildId(pluginSlug, existingBuildId),
            BuildStates.Failed,
            new JObject { ["error"] = "Existing failed build" });

        using var dashboardResponse = await client.GetAsync($"/plugins/{pluginSlug}");
        dashboardResponse.EnsureSuccessStatusCode();
        var dashboard = await dashboardResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("id=\"CreateNewBuild\"", dashboard, StringComparison.Ordinal);
        Assert.DoesNotContain(">Retry</a>", dashboard, StringComparison.Ordinal);

        using var buildResponse = await client.GetAsync($"/plugins/{pluginSlug}/builds/{existingBuildId}");
        buildResponse.EnsureSuccessStatusCode();
        Assert.DoesNotContain(">Retry</a>", await buildResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NewBuildsDisabled_DuringApiRepositoryLookup_DoesNotCreateBuild()
    {
        const string password = "123456";
        await using var tester = Create("BuildApiRace");
        tester.ReuseDatabase = false;
        var gitProvider = new BlockingGitHostingProvider();
        tester.ConfigureServices = services =>
        {
            services.RemoveAll<IGitHostingProvider>();
            services.AddSingleton<IGitHostingProvider>(gitProvider);
        };
        await tester.Start();

        var email = $"build-api-race-{Guid.NewGuid():N}@example.com";
        var ownerId = await tester.CreateFakeUserAsync(email, password);
        var pluginSlug = new PluginSlug("api-race-" + Guid.NewGuid().ToString("N")[..8]);
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.NewPlugin(pluginSlug, ownerId);

        using var client = tester.CreateHttpClient().SetBasicAuth(email, password);
        using var content = CreateBuildJson(gitProvider.RepositoryUrl);
        var request = client.PostAsync($"/api/v1/plugins/{pluginSlug}/builds", content);
        await AssertBuildRejectedWhenDisabledDuringRepositoryLookup(
            tester, conn, pluginSlug, gitProvider, request);
    }

    [Fact]
    public async Task NewBuildsDisabled_DuringUiRepositoryLookup_DoesNotCreateBuild()
    {
        const string password = "123456";
        await using var tester = Create("BuildUiRace");
        tester.ReuseDatabase = false;
        var gitProvider = new BlockingGitHostingProvider();
        tester.ConfigureServices = services =>
        {
            services.RemoveAll<IGitHostingProvider>();
            services.AddSingleton<IGitHostingProvider>(gitProvider);
        };
        await tester.Start();

        var email = $"build-ui-race-{Guid.NewGuid():N}@example.com";
        var ownerId = await tester.CreateFakeUserAsync(email, password);
        var pluginSlug = new PluginSlug("ui-race-" + Guid.NewGuid().ToString("N")[..8]);
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.NewPlugin(pluginSlug, ownerId);

        using var client = CreateBrowserClient(tester);
        await LogIn(client, email, password);

        var createUrl = $"/plugins/{pluginSlug}/create";
        using var createPage = await client.GetAsync(createUrl);
        createPage.EnsureSuccessStatusCode();
        var antiforgeryToken = ExtractAntiforgeryToken(await createPage.Content.ReadAsStringAsync());
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["GitRepository"] = gitProvider.RepositoryUrl,
            ["GitRef"] = "main",
            ["BuildConfig"] = ServerTester.BuildCfg,
            ["__RequestVerificationToken"] = antiforgeryToken
        });

        var request = client.PostAsync(createUrl, form);
        await AssertBuildRejectedWhenDisabledDuringRepositoryLookup(
            tester, conn, pluginSlug, gitProvider, request);
    }

    [Fact]
    public async Task FeatureFlags_AppearInSettingsEditorAndUpdatesTakeEffectImmediately()
    {
        const string password = "123456";
        await using var tester = Create("FlagSettingsEditor");
        tester.ReuseDatabase = false;
        await tester.Start();

        var email = $"feature-admin-{Guid.NewGuid():N}@example.com";
        var adminId = await tester.CreateFakeUserAsync(email, password);
        var pluginSlug = new PluginSlug("flag-editor-" + Guid.NewGuid().ToString("N")[..8]);
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.ExecuteAsync(
            """
            INSERT INTO "AspNetUserRoles" ("UserId", "RoleId")
            SELECT @userId, "Id" FROM "AspNetRoles" WHERE "NormalizedName" = 'SERVERADMIN'
            """,
            new { userId = adminId });
        await conn.NewPlugin(pluginSlug, adminId);

        using var client = CreateBrowserClient(tester);
        await LogIn(client, email, password);

        using var editorResponse = await client.GetAsync("/admin/SettingsEditor");
        editorResponse.EnsureSuccessStatusCode();
        var editor = await editorResponse.Content.ReadAsStringAsync();
        Assert.Contains($"data-key=\"{SettingsKeys.RegistrationEnabled}\"", editor, StringComparison.Ordinal);
        Assert.Contains($"data-key=\"{SettingsKeys.NewBuildsEnabled}\"", editor, StringComparison.Ordinal);
        var antiforgeryToken = ExtractAntiforgeryToken(editor);

        using (var registrationForm = SettingsEditorForm(SettingsKeys.RegistrationEnabled, "false", antiforgeryToken))
        using (var registrationResponse = await client.PostAsync("/admin/SettingsEditor", registrationForm))
        {
            Assert.Equal(HttpStatusCode.Redirect, registrationResponse.StatusCode);
        }

        var settings = tester.GetService<AdminSettingsCache>();
        Assert.Equal("false", await conn.SettingsGetAsync(SettingsKeys.RegistrationEnabled));
        Assert.False(settings.RegistrationEnabled);
        using (var registrationResponse = await client.GetAsync("/register"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, registrationResponse.StatusCode);
        }

        using (var buildsForm = SettingsEditorForm(SettingsKeys.NewBuildsEnabled, "false", antiforgeryToken))
        using (var buildsResponse = await client.PostAsync("/admin/SettingsEditor", buildsForm))
        {
            Assert.Equal(HttpStatusCode.Redirect, buildsResponse.StatusCode);
        }

        Assert.Equal("false", await conn.SettingsGetAsync(SettingsKeys.NewBuildsEnabled));
        Assert.False(settings.NewBuildsEnabled);
        using var createResponse = await client.GetAsync($"/plugins/{pluginSlug}/create");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, createResponse.StatusCode);
    }

    [Fact]
    public async Task NewBuildsDisabled_KeepsExistingBuildOperationsAvailable()
    {
        const string password = "123456";
        await using var tester = Create("ExistingBuildOps");
        tester.ReuseDatabase = false;
        await tester.Start();

        var email = $"existing-build-{Guid.NewGuid():N}@example.com";
        var ownerId = await tester.CreateFakeUserAsync(email, password);
        var pluginSlug = new PluginSlug("existing-" + Guid.NewGuid().ToString("N")[..8]);
        var version = PluginVersion.Parse("1.0.0");
        var artifactUrl = "https://example.invalid/plugin.zip";
        var manifest = PluginManifest.Parse($$"""
            {
              "Identifier": "FeatureFlags.{{pluginSlug}}",
              "Name": "Existing build",
              "Version": "{{version}}",
              "Description": "Feature flag coverage",
              "Dependencies": []
            }
            """);

        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.NewPlugin(pluginSlug, ownerId);
        var buildId = await conn.NewBuild(pluginSlug, new PluginBuildParameters("https://example.invalid/repository.git"));
        var fullBuildId = new FullBuildId(pluginSlug, buildId);
        await conn.UpdateBuild(fullBuildId, BuildStates.Uploaded, new JObject
        {
            ["url"] = artifactUrl,
            ["gitCommit"] = "0123456789abcdef"
        }, manifest);
        Assert.True(await conn.SetVersionBuild(fullBuildId, version, null, null, true));

        using var browser = CreateBrowserClient(tester);
        await LogIn(browser, email, password);
        using var api = tester.CreateHttpClient().SetBasicAuth(email, password);
        await DisableFeature(tester, conn, SettingsKeys.NewBuildsEnabled);

        string uiAntiforgeryToken;
        using (var uiDetails = await browser.GetAsync($"/plugins/{pluginSlug}/builds/{buildId}"))
        {
            Assert.Equal(HttpStatusCode.OK, uiDetails.StatusCode);
            var html = await uiDetails.Content.ReadAsStringAsync();
            Assert.DoesNotContain(">Retry</a>", html, StringComparison.Ordinal);
            Assert.Contains($"href=\"{artifactUrl}\"", html, StringComparison.Ordinal);
            Assert.Contains(">Release</button>", html, StringComparison.Ordinal);
            uiAntiforgeryToken = ExtractAntiforgeryToken(html);
        }

        using (var apiDetails = await api.GetAsync($"/api/v1/plugins/{pluginSlug}/builds/{buildId}"))
        {
            Assert.Equal(HttpStatusCode.OK, apiDetails.StatusCode);
        }

        using (var download = await browser.GetAsync($"/api/v1/plugins/{pluginSlug}/versions/{version}/download"))
        {
            Assert.Equal(HttpStatusCode.Redirect, download.StatusCode);
            Assert.Equal(artifactUrl, download.Headers.Location?.AbsoluteUri);
        }

        using (var releaseForm = VersionCommandForm("release", uiAntiforgeryToken))
        using (var release = await browser.PostAsync($"/plugins/{pluginSlug}/versions/{version}/release", releaseForm))
        {
            Assert.Equal(HttpStatusCode.Redirect, release.StatusCode);
        }
        Assert.False(await IsPreRelease(conn, pluginSlug, version));

        using (var unreleaseForm = VersionCommandForm("unrelease", uiAntiforgeryToken))
        using (var unrelease = await browser.PostAsync($"/plugins/{pluginSlug}/versions/{version}/release", unreleaseForm))
        {
            Assert.Equal(HttpStatusCode.Redirect, unrelease.StatusCode);
        }
        Assert.True(await IsPreRelease(conn, pluginSlug, version));

        using (var releaseBody = new StringContent("{}", Encoding.UTF8, "application/json"))
        using (var release = await api.PostAsync($"/api/v1/plugins/{pluginSlug}/versions/{version}/release", releaseBody))
        {
            Assert.Equal(HttpStatusCode.OK, release.StatusCode);
        }
        Assert.False(await IsPreRelease(conn, pluginSlug, version));

        using (var unrelease = await api.PostAsync($"/api/v1/plugins/{pluginSlug}/versions/{version}/unrelease", null))
        {
            Assert.Equal(HttpStatusCode.OK, unrelease.StatusCode);
        }
        Assert.True(await IsPreRelease(conn, pluginSlug, version));
    }

    [Fact]
    public async Task NewBuildsDisabled_StopsAlreadyQueuedBuildBeforeSandboxPreparation()
    {
        TaskCompletionSource releasePreparation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var sandbox = new AdmissionTestSandbox { PreparationBlockedUntil = releasePreparation.Task };
        await using var tester = Create("BuildsQueuedFlag");
        tester.ReuseDatabase = false;
        tester.ConfigureServices = sandbox.Register;
        await tester.Start();
        tester.GetService<BuildExecutorState>().MarkReady(
            "sha256:1111111111111111111111111111111111111111111111111111111111111111",
            "sha256:2222222222222222222222222222222222222222222222222222222222222222");

        var ownerId = await tester.CreateFakeUserAsync();
        var pluginSlug = new PluginSlug("queued-disabled-" + Guid.NewGuid().ToString("N")[..8]);
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.NewPlugin(pluginSlug, ownerId);
        List<FullBuildId> fullBuildIds = [];
        for (var i = 0; i < BuildPolicy.MaxConcurrentBuilds + 1; i++)
        {
            var buildId = await conn.NewBuild(
                pluginSlug,
                new PluginBuildParameters("https://github.com/example/repository"));
            fullBuildIds.Add(new FullBuildId(pluginSlug, buildId));
        }

        var queuedBuild = fullBuildIds[^1];
        var buildService = tester.GetService<BuildService>();
        Task[] blockerTasks = [];
        Task? queuedTask = null;
        try
        {
            blockerTasks = fullBuildIds.Take(BuildPolicy.MaxConcurrentBuilds)
                .Select(buildService.Build)
                .ToArray();
            await sandbox.WaitForPreparationStartsAsync(BuildPolicy.MaxConcurrentBuilds);

            queuedTask = buildService.Build(queuedBuild);
            Assert.False(queuedTask.IsCompleted);

            await DisableFeature(tester, conn, SettingsKeys.NewBuildsEnabled);
            releasePreparation.TrySetResult();
            await queuedTask.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAll(blockerTasks).WaitAsync(TimeSpan.FromSeconds(10));

            var build = await conn.QuerySingleAsync<(string state, string error)>(
                "SELECT state, build_info->>'error' AS error FROM builds WHERE plugin_slug = @pluginSlug AND id = @buildId",
                new { pluginSlug = pluginSlug.ToString(), buildId = queuedBuild.BuildId });
            Assert.Equal(BuildStates.Failed.ToEventName(), build.state);
            Assert.Equal("Plugin builds are temporarily disabled.", build.error);
            Assert.DoesNotContain(queuedBuild, sandbox.StartedPreparations.Keys);
            Assert.Equal(BuildPolicy.MaxConcurrentBuilds, sandbox.StartedPreparations.Count);
            Assert.All(sandbox.StartedPreparations.Values, prepared =>
            {
                Assert.False(prepared.Started);
                Assert.True(prepared.Disposed);
            });
        }
        finally
        {
            releasePreparation.TrySetResult();
            if (queuedTask is not null)
                await Record.ExceptionAsync(() => queuedTask.WaitAsync(TimeSpan.FromSeconds(10)));
            foreach (var blockerTask in blockerTasks)
                await Record.ExceptionAsync(() => blockerTask.WaitAsync(TimeSpan.FromSeconds(10)));
        }
    }

    [Fact]
    public async Task NewBuildsDisabled_DuringBrokerPreparation_DoesNotStartWorker()
    {
        await using var broker = await BuildBrokerSecurityTests.BrokerFixture.Start();
        TaskCompletionSource preparing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource allowPreparation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        broker.Sandbox.BeforePrepare = () => preparing.TrySetResult();
        broker.Sandbox.PreparationBlockedUntil = allowPreparation.Task;
        await using var tester = Create("BuildFlagDuringBrokerPreparation");
        tester.ReuseDatabase = false;
        tester.ConfigureServices = services =>
        {
            foreach (var service in services.Where(service => service.ServiceType == typeof(IHostedService) &&
                         (service.ImplementationType == typeof(BuildBrokerMonitor) ||
                          service.ImplementationType == typeof(AzureStartupHostedService))).ToArray())
                services.Remove(service);
            services.RemoveAll<RemoteBuildSandbox>();
            services.AddSingleton(provider => broker.CreateRemoteSandbox(provider.GetRequiredService<BuildExecutorState>()));
            services.RemoveAll<IBuildSandbox>();
            services.AddSingleton<IBuildSandbox>(provider => provider.GetRequiredService<RemoteBuildSandbox>());
            services.RemoveAll<IGitHostingProvider>();
        };
        await tester.Start();
        tester.GetService<BuildExecutorState>().MarkReady(
            broker.Executor.Snapshot.WorkerImageId!, broker.Executor.Snapshot.ProxyImageId!);

        var ownerId = await tester.CreateFakeUserAsync();
        var pluginSlug = new PluginSlug("mid-prepare-" + Guid.NewGuid().ToString("N")[..8]);
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.NewPlugin(pluginSlug, ownerId);
        var buildId = await conn.NewBuild(pluginSlug,
            new PluginBuildParameters("https://github.com/example/repository"));
        var fullBuildId = new FullBuildId(pluginSlug, buildId);
        var states = new ConcurrentQueue<string>();
        using var subscription = tester.GetService<EventAggregator>().Subscribe<BuildChanged>(evt =>
        {
            if (evt.FullBuildId == fullBuildId)
                states.Enqueue(evt.EventName);
        });

        var buildTask = tester.GetService<BuildService>().Build(fullBuildId);
        try
        {
            await preparing.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(buildTask.IsCompleted);
            Assert.Equal(1, broker.Sandbox.PrepareCalls);
            await DisableFeature(tester, conn, SettingsKeys.NewBuildsEnabled);
            allowPreparation.TrySetResult();
            await buildTask.WaitAsync(TimeSpan.FromSeconds(10));

            var build = await conn.QuerySingleAsync<(string state, string error, string? url)>(
                "SELECT state, build_info->>'error' AS error, build_info->>'url' AS url FROM builds WHERE plugin_slug = @pluginSlug AND id = @buildId",
                new { pluginSlug = pluginSlug.ToString(), buildId });
            Assert.Equal(BuildStates.Failed.ToEventName(), build.state);
            Assert.Equal("Plugin builds are temporarily disabled.", build.error);
            Assert.Null(build.url);
            Assert.False(broker.Sandbox.Prepared[buildId].Started.Task.IsCompleted);
            Assert.True(broker.Sandbox.Prepared[buildId].IsDisposed);
            Assert.Equal(1, broker.Sandbox.DisposalCount);
            Assert.True(broker.Executor.Snapshot.IsReady);
            Assert.DoesNotContain(states, state => state == BuildStates.WaitingUpload.ToEventName() ||
                state == BuildStates.Uploading.ToEventName() || state == BuildStates.Uploaded.ToEventName());
            Assert.False(await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM versions WHERE plugin_slug = @pluginSlug)",
                new { pluginSlug = pluginSlug.ToString() }));
        }
        finally
        {
            allowPreparation.TrySetResult();
            // This fixture deliberately removed the hosted broker monitor, so
            // cancel its client explicitly if an assertion interrupted the build.
            if (!buildTask.IsCompleted)
                await Record.ExceptionAsync(() => tester.GetService<RemoteBuildSandbox>()
                    .StopAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            try { await buildTask.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { /* Preserve the original failure while releasing preparation. */ }
        }
    }

    private static StringContent CreateBuildJson(string repositoryUrl)
    {
        return new StringContent(
            new JObject
            {
                ["gitRepository"] = repositoryUrl,
                ["gitRef"] = "main",
                ["buildConfig"] = ServerTester.BuildCfg
            }.ToString(),
            Encoding.UTF8,
            "application/json");
    }

    private static FormUrlEncodedContent SettingsEditorForm(string key, string value, string antiforgeryToken)
    {
        return new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["key"] = key,
            ["value"] = value,
            ["__RequestVerificationToken"] = antiforgeryToken
        });
    }

    private static FormUrlEncodedContent VersionCommandForm(string command, string antiforgeryToken)
    {
        return new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["command"] = command,
            ["__RequestVerificationToken"] = antiforgeryToken
        });
    }

    private static HttpClient CreateBrowserClient(ServerTester tester)
    {
        return new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            CookieContainer = new CookieContainer()
        })
        {
            BaseAddress = new Uri(tester.WebApp.Urls.First(), UriKind.Absolute)
        };
    }

    private static async Task<bool> HasBuildRows(Npgsql.NpgsqlConnection conn, PluginSlug pluginSlug)
    {
        return await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS(SELECT 1 FROM builds WHERE plugin_slug = @pluginSlug)
                OR EXISTS(SELECT 1 FROM builds_ids WHERE plugin_slug = @pluginSlug)
            """,
            new { pluginSlug = pluginSlug.ToString() });
    }

    private static async Task AssertBuildRejectedWhenDisabledDuringRepositoryLookup(
        ServerTester tester,
        Npgsql.NpgsqlConnection conn,
        PluginSlug pluginSlug,
        BlockingGitHostingProvider gitProvider,
        Task<HttpResponseMessage> request)
    {
        const string identifier = "FeatureFlags.RepositoryRace";
        try
        {
            await gitProvider.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, gitProvider.FetchCount);
            await DisableFeature(tester, conn, SettingsKeys.NewBuildsEnabled);
            gitProvider.Release(identifier);

            using var response = await request.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.False(await HasBuildRows(conn, pluginSlug));
        }
        finally
        {
            gitProvider.Release(identifier);
            if (!request.IsCompleted)
                try
                {
                    using var response = await request.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch
                {
                    // Preserve the original failure while releasing the blocked request.
                }
        }
    }

    private static async Task<bool> IsPreRelease(
        Npgsql.NpgsqlConnection conn,
        PluginSlug pluginSlug,
        PluginVersion version)
    {
        return await conn.ExecuteScalarAsync<bool>(
            "SELECT pre_release FROM versions WHERE plugin_slug = @pluginSlug AND ver = @version",
            new { pluginSlug = pluginSlug.ToString(), version = version.VersionParts });
    }

    private static async Task DisableFeature(ServerTester tester, Npgsql.NpgsqlConnection conn, string key)
    {
        await conn.SettingsSetAsync(key, "false");
        await tester.GetService<AdminSettingsCache>().RefreshFeatureSettings(conn);
    }

    private static async Task LogIn(HttpClient client, string email, string password)
    {
        using var loginPage = await client.GetAsync("/login");
        loginPage.EnsureSuccessStatusCode();
        var antiforgeryToken = ExtractAntiforgeryToken(await loginPage.Content.ReadAsStringAsync());

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
            ["__RequestVerificationToken"] = antiforgeryToken
        });
        using var response = await client.PostAsync("/login", form);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static string ExtractAntiforgeryToken(string html)
    {
        var input = Regex.Match(
            html,
            "<input\\b(?=[^>]*\\bname=\"__RequestVerificationToken\")[^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        Assert.True(input.Success, "HTML did not contain an antiforgery token input.");

        var value = Regex.Match(
            input.Value,
            "\\bvalue=\"([^\"]+)\"",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        Assert.True(value.Success, "Antiforgery token input did not contain a value.");
        return WebUtility.HtmlDecode(value.Groups[1].Value);
    }

    private sealed class BlockingGitHostingProvider : IGitHostingProvider
    {
        private readonly TaskCompletionSource<bool> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _fetchCount;

        public string RepositoryUrl { get; } = $"https://github.com/feature-flags-{Guid.NewGuid():N}/repository";
        public int FetchCount => Volatile.Read(ref _fetchCount);
        public Task Entered => _entered.Task;

        public bool CanHandle(string repoUrl)
        {
            return string.Equals(repoUrl, RepositoryUrl, StringComparison.Ordinal);
        }

        public async Task<string> FetchIdentifierFromCsprojAsync(string repoUrl, string gitRef, string? pluginDir = null)
        {
            Interlocked.Increment(ref _fetchCount);
            _entered.TrySetResult(true);
            return await _release.Task;
        }

        public void Release(string identifier)
        {
            _release.TrySetResult(identifier);
        }

        public Task<List<GitHubContributor>> GetContributorsAsync(string repoUrl, string pluginDir)
        {
            return Task.FromResult(new List<GitHubContributor>());
        }

        public string? GetSourceUrl(string repoUrl, string? commit, string? pluginDir)
        {
            return null;
        }

        public (string Owner, string RepoName)? ParseRepository(string repoUrl)
        {
            return null;
        }
    }
}
