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
using Npgsql;
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
    private string? _dbname;
    private string? _serverConnString;

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
        var maxAttempts = ReuseDatabase ? 1 : 3;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var dbName = ReuseDatabase
                ? baseName
                : $"{baseName}_{Guid.NewGuid():N}".ToLowerInvariant();

            Logs.LogInformation("DbName: {dbName} (attempt {attempt})", dbName, attempt);

            var connStr = "User ID=postgres;Include Error Detail=true;Host=127.0.0.1;Port=61932;Database=" + dbName;
            Environment.SetEnvironmentVariable("PB_POSTGRES", connStr);
            Environment.SetEnvironmentVariable("PB_STORAGE_CONNECTION_STRING",
                "BlobEndpoint=http://127.0.0.1:32827/satoshi;AccountName=satoshi;AccountKey=Rxb41pUHRe+ibX5XS311tjXpjvu7mVi2xYJvtmq1j2jlUpN+fY/gkzyBMjqwzgj42geXGdYSbPEcu5i5wjSjPw==");

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
            webappBuilder.Services.AddHttpClient();
            webappBuilder.Logging.AddFilter(typeof(ProcessRunner).FullName, LogLevel.Trace);
            webappBuilder.Logging.AddProvider(Logs);
            ConfigureServices?.Invoke(webappBuilder.Services);

            var webapp = webappBuilder.Build();
            host.Configure(webapp);

            try
            {
                disposables.Add(webapp);
                await webapp.StartAsync();
                _WebApp = webapp;

                await using var conn = await GetService<DBConnectionFactory>().Open();
                await conn.ReloadTypesAsync();
                await conn.SettingsSetAsync(SettingsKeys.VerifiedGithub, "true");
                var verfCache = GetService<UserVerifiedCache>();
                await verfCache.RefreshAllUserVerifiedSettings(conn);

                return;
            }
            catch (Exception ex) when (IsPgDbNameConflict(ex) && !ReuseDatabase)
            {
                Logs.LogInformation("DB name conflict detected, retrying with a new database name...");
                try { await webapp.DisposeAsync(); } catch { /* ignore */ }
                disposables.Remove(webapp);
                _WebApp = null;
            }
        }

        throw new InvalidOperationException("Failed to start test server after multiple DB name retries.");
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

    private static bool IsPgDbNameConflict(Exception? ex) =>
        ex switch
        {
            null => false,
            PostgresException { SqlState: "42P04" } => true,
            AggregateException a => a.InnerExceptions.Any(IsPgDbNameConflict),
            _ => IsPgDbNameConflict(ex.InnerException)
        };

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
}
