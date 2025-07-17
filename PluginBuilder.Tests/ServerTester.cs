using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;

namespace PluginBuilder.Tests;

public class ServerTester : IAsyncDisposable
{
    private readonly List<IAsyncDisposable> disposables = new();
    private WebApplication? _WebApp;
    public int Port { get; set; } = Utils.FreeTcpPort();


    public ServerTester(string testFolder, XUnitLogger logs)
    {
        TestFolder = testFolder;
        Logs = logs;
    }

    public string TestFolder { get; }

    public XUnitLogger Logs { get; }

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
        var webapp = webappBuilder.Build();
        host.Configure(webapp);
        disposables.Add(webapp);
        await webapp.StartAsync();
        _WebApp = webapp;
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
        string slug = "rockstar-stylist",
        string gitRef = "plugins/collection2",
        string pluginDir = "Plugins/BTCPayServer.Plugins.RockstarStylist")
    {
        var conn = await GetService<DBConnectionFactory>().Open();
        var buildService = GetService<BuildService>();

        await conn.NewPlugin(slug);
        var buildId = await conn.NewBuild(slug, new PluginBuildParameters("https://github.com/NicolasDorier/btcpayserver")
        {
            GitRef = gitRef,
            PluginDirectory = pluginDir
        });

        var fullBuildId = new FullBuildId(slug, buildId);
        await buildService.Build(fullBuildId);
        return fullBuildId;
    }

}
