using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using PluginBuilder.Events;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;

namespace PluginBuilder.Tests;

public class ServerTester : IAsyncDisposable
{
    private readonly List<IAsyncDisposable> disposables = new();
    private WebApplication? _WebApp;
    public int Port { get; set; } = Utils.FreeTcpPort();
    private string? _dbname;
    private string? _serverConnString;

    public const string RepoUrl   = "https://github.com/NicolasDorier/btcpayserver";
    public const string GitRef    = "plugins/collection2";
    public const string PluginDir = "Plugins/BTCPayServer.Plugins.RockstarStylist";
    public const string BuildCfg  = "Release";
    public const string PluginSlug = "rockstar-stylist";

    private const string StorageConnectionString = "BlobEndpoint=http://127.0.0.1:32827/satoshi;AccountName=satoshi;AccountKey=Rxb41pUHRe+ibX5XS311tjXpjvu7mVi2xYJvtmq1j2jlUpN+fY/gkzyBMjqwzgj42geXGdYSbPEcu5i5wjSjPw==";


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

        // If we are not reusing the database, drop the test database to keep environments clean
        if (!ReuseDatabase && !string.IsNullOrEmpty(_dbname) && !string.IsNullOrEmpty(_serverConnString))
        {
            try
            {
                await DropDatabaseAsync();
                Logs.LogInformation("Dropped test database {db}", _dbname);
            }
            catch (Exception ex)
            {
                Logs.LogInformation("Could not drop test database {db}: {message}", _dbname, ex.Message);
            }
            finally
            {
                _dbname = null;
                _serverConnString = null;
            }
        }

        foreach (var d in disposables) await d.DisposeAsync();
    }

    public T GetService<T>() where T : notnull
    {
        return WebApp.Services.GetRequiredService<T>();
    }

    public async Task Start()
    {
        var baseName = TestFolder.ToLowerInvariant();
        var dbName = ReuseDatabase
            ? baseName
            : $"{baseName}_{Guid.NewGuid():N}".ToLowerInvariant();

        Logs.LogInformation("DbName: {dbName}", dbName);

        var connStr = "User ID=postgres;Include Error Detail=true;Host=127.0.0.1;Port=61932;Database=" + dbName;

        // Track the database used for this test run to allow cleanup
        _dbname = dbName;
        var csb = new NpgsqlConnectionStringBuilder(connStr) { Database = "postgres" };
        _serverConnString = csb.ToString();

        Program host = new();
        var projectDir = FindPluginBuilderDirectory();
        var webappBuilder = host.CreateWebApplicationBuilder(new WebApplicationOptions
        {
            ContentRootPath = projectDir,
            WebRootPath = Path.Combine(projectDir, "wwwroot"),
            Args = [$"--urls=http://127.0.0.1:{Port}"]
        });

        // Inject configuration directly instead of using environment variables to avoid cross-test contamination
        webappBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["POSTGRES"] = connStr,
            ["STORAGE_CONNECTION_STRING"] = StorageConnectionString
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
        await using var conn = await GetService<DBConnectionFactory>().Open();
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

    public async Task<string> CreateFakeUserAsync(string? email = null, string? password = "123456", bool confirmEmail = true, bool githubVerified = true)
    {
        using var scope = WebApp.Services.CreateScope();
        var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();

        email ??= $"u{Guid.NewGuid():N}@a.com";
        password ??= "123456";
        var user = new IdentityUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = confirmEmail
        };
        var res = await userMgr.CreateAsync(user, password);
        if (!res.Succeeded)
            throw new InvalidOperationException("Failed to create test user: " + string.Join(", ", res.Errors.Select(e => e.Description)));

        if (!githubVerified) return user.Id;

        await using var conn = await GetService<DBConnectionFactory>().Open();
        await conn.VerifyGithubAccount(user.Id, "https://gist.github.com/dummy/123");

        return user.Id;
    }


    private async Task DropDatabaseAsync()
    {
        if (string.IsNullOrEmpty(_serverConnString) || string.IsNullOrEmpty(_dbname))
            return;

        await using var conn = new NpgsqlConnection(_serverConnString);
        await conn.OpenAsync();

        // Terminate all remaining connections to the target DB (including idle ones)
        await using (var terminate = new NpgsqlCommand(
                         "select pg_terminate_backend(pid) from pg_stat_activity where datname = @db and pid <> pg_backend_pid();",
                         conn))
        {
            terminate.Parameters.AddWithValue("db", _dbname);
            await terminate.ExecuteNonQueryAsync();
        }

        var safeDb = _dbname.Replace("\"", "\"\"");
        var dropSql = $"DROP DATABASE IF EXISTS \"{safeDb}\";";
        await using (var drop = new NpgsqlCommand(dropSql, conn))
        {
            await drop.ExecuteNonQueryAsync();
        }
    }

    public async Task<BuildStates> WaitForBuildToFinishAsync(
        FullBuildId id,
        TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromMinutes(5);

        var agg = GetService<EventAggregator>();
        var tcs = new TaskCompletionSource<BuildStates>(TaskCreationOptions.RunContinuationsAsynchronously);

        IDisposable? sub = agg.Subscribe<BuildChanged>(e =>
        {
            if (!e.FullBuildId.Equals(id)) return;
            var state = BuildStatesExtensions.FromEventName(e.EventName);
            if (state.IsTerminal()) tcs.TrySetResult(state);
        });

        using var cts = new CancellationTokenSource(timeout.Value);
        using var _ = cts.Token.Register(() => tcs.TrySetCanceled(cts.Token));

        try
        {
            return await tcs.Task;
        }
        finally
        {
            sub?.Dispose();
        }
    }
}
