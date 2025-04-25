using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PluginBuilder.Tests;

public class ServerTester : IAsyncDisposable
{
    private readonly List<IAsyncDisposable> disposables = new();
    private WebApplication? _WebApp;

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
        var webappBuilder = host.CreateWebApplicationBuilder();
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
}
