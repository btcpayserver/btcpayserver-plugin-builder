using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.DataModels;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;

namespace PluginBuilder.Tests;

public class ServerTester : IAsyncDisposable
{
    private readonly List<IAsyncDisposable> disposables = new();
    private WebApplication? _WebApp;
    public int Port { get; set; } = Utils.FreeTcpPort();

    public const string RepoUrl   = "https://github.com/NicolasDorier/btcpayserver";
    public const string GitRef    = "plugins/collection2";
    public const string PluginDir = "Plugins/BTCPayServer.Plugins.RockstarStylist";
    public const string BuildCfg  = "Release";
    public const string PluginSlug = "rockstar-stylist";


    public ServerTester(string testFolder, XUnitLogger logs)
    {
        TestFolder = testFolder;
        Logs = logs;
    }

    public string TestFolder { get; }

    public XUnitLogger Logs { get; }

    public Action<IServiceCollection>? ConfigureServices { get; set; }

    public WebApplication WebApp => _WebApp ?? throw new InvalidOperationException("Webapp not initialized");

    public bool ReuseDatabase { get; set; } = true;

    public async ValueTask DisposeAsync()
    {
        if (_WebApp != null)
        {
            await _WebApp.StopAsync();
            await _WebApp.DisposeAsync();
            _WebApp = null;
        }

        foreach (var d in disposables) await d.DisposeAsync();
    }

    public T GetService<T>() where T : notnull
    {
        return WebApp.Services.GetRequiredService<T>();
    }

    public async Task Start()
    {
        var dbName = TestFolder;
        if (!ReuseDatabase) dbName = TestFolder + "_" + DateTimeOffset.UtcNow.Ticks / 100000;
        dbName = dbName.ToLowerInvariant();
        Logs.LogInformation($"DbName: {dbName}");
        Environment.SetEnvironmentVariable("PB_POSTGRES", "User ID=postgres;Include Error Detail=true;Host=127.0.0.1;Port=61932;Database=" + dbName);
        Environment.SetEnvironmentVariable("PB_STORAGE_CONNECTION_STRING",
            "BlobEndpoint=http://127.0.0.1:32827/satoshi;AccountName=satoshi;AccountKey=Rxb41pUHRe+ibX5XS311tjXpjvu7mVi2xYJvtmq1j2jlUpN+fY/gkzyBMjqwzgj42geXGdYSbPEcu5i5wjSjPw==");
        Program host = new();
        var projectDir = FindPluginBuilderDirectory();
        var webappBuilder = host.CreateWebApplicationBuilder(new WebApplicationOptions
        {
            ContentRootPath = projectDir,
            WebRootPath = Path.Combine(projectDir, "wwwroot"),
            Args = [$"--urls=http://127.0.0.1:{Port}"]
        });
        webappBuilder.Services.AddHttpClient();

        webappBuilder.Logging.AddFilter(typeof(ProcessRunner).FullName, LogLevel.Trace);
        webappBuilder.Logging.AddProvider(Logs);
        ConfigureServices?.Invoke(webappBuilder.Services);
        var webapp = webappBuilder.Build();
        host.Configure(webapp);
        disposables.Add(webapp);
        await webapp.StartAsync();
        _WebApp = webapp;

        await using var conn = await GetService<DBConnectionFactory>().Open();
        await conn.ReloadTypesAsync();
        await conn.SettingsSetAsync(SettingsKeys.VerifiedGithub, "true");
        var verfCache = GetService<UserVerifiedCache>();
        await verfCache.RefreshAllUserVerifiedSettings(conn);
    }

    public HttpClient CreateHttpClient()
    {
        var client = GetService<IHttpClientFactory>().CreateClient();
        client.BaseAddress = new Uri(WebApp.Urls.First(), UriKind.Absolute);
        return client;
    }

    private string FindPluginBuilderDirectory()
    {
        var solutionDirectory = TryGetSolutionDirectoryInfo();
        return Path.Combine(solutionDirectory.FullName, "PluginBuilder");
    }

    private DirectoryInfo TryGetSolutionDirectoryInfo()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory != null && !directory.GetFiles("btcpayserver-plugin-builder.sln").Any())
        {
            directory = directory.Parent;
        }

        if (directory == null)
            throw new InvalidOperationException("Could not find the solution directory.");

        return directory;
    }

    public async Task<FullBuildId> CreateAndBuildPluginAsync(
        string userId,
        string slug = PluginSlug,
        string gitRef = GitRef,
        string pluginDir = PluginDir)
    {
        var conn = await GetService<DBConnectionFactory>().Open();
        var buildService = GetService<BuildService>();

        await conn.NewPlugin(slug, userId);
        var buildId = await conn.NewBuild(slug, new PluginBuildParameters(RepoUrl)
        {
            GitRef = gitRef,
            PluginDirectory = pluginDir
        });

        var fullBuildId = new FullBuildId(slug, buildId);
        await buildService.Build(fullBuildId);
        return fullBuildId;
    }

    public async Task<string> CreateFakeUserAsync(string? email = null, bool confirmEmail = true, bool githubVerified = true)
    {
        using var scope = WebApp.Services.CreateScope();
        var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();

        email ??= $"u{Guid.NewGuid():N}@a.com";
        var user = new IdentityUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = confirmEmail
        };
        var res = await userMgr.CreateAsync(user, "Test1234!");
        if (!res.Succeeded)
            throw new InvalidOperationException("Failed to create test user: " + string.Join(", ", res.Errors.Select(e => e.Description)));

        if (!githubVerified) return user.Id;

        await using var conn = await GetService<DBConnectionFactory>().Open();
        await conn.VerifyGithubAccount(user.Id, "https://gist.github.com/dummy/123");

        return user.Id;
    }
}
