using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace PluginBuilder.Tests
{
    public class ServerTester : IAsyncDisposable
    {
        public ServerTester(string testFolder, XUnitLogger logs)
        {
            TestFolder = testFolder;
            Logs = logs;
        }

        public string TestFolder { get; }

        public T GetService<T>() where T : notnull
        {
            return WebApp.Services.GetRequiredService<T>();
        }

        public XUnitLogger Logs { get; }

        List<IAsyncDisposable> disposables = new List<IAsyncDisposable>();
        WebApplication? _WebApp;
        public WebApplication WebApp
        {
            get
            {
                return _WebApp ?? throw new InvalidOperationException("Webapp not initialized");
            }
        }
        public bool ReuseDatabase { get; set; } = true;
        public async Task Start()
        {
            string dbName = TestFolder;
            if (!ReuseDatabase)
            {
                dbName = TestFolder + "_" + (DateTimeOffset.UtcNow.Ticks / 100000).ToString();
            }
            dbName = dbName.ToLowerInvariant();
            Logs.LogInformation($"DbName: {dbName}");
            Environment.SetEnvironmentVariable("PB_POSTGRES", "User ID=postgres;Include Error Detail=true;Host=127.0.0.1;Port=61932;Database=" + dbName);
            Environment.SetEnvironmentVariable("PB_STORAGE_CONNECTION_STRING", "BlobEndpoint=http://127.0.0.1:32827/satoshi;AccountName=satoshi;AccountKey=Rxb41pUHRe+ibX5XS311tjXpjvu7mVi2xYJvtmq1j2jlUpN+fY/gkzyBMjqwzgj42geXGdYSbPEcu5i5wjSjPw==");
            var host = new PluginBuilder.Program();
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

        public async ValueTask DisposeAsync()
        {
            foreach (var d in disposables)
            {
                await d.DisposeAsync();
            }
        }
    }
}
