using System;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using PluginBuilder.Services;
using Xunit;

namespace PluginBuilder.Tests;

public class PlaywrightTester : IAsyncDisposable
{
    public ServerTester Server { get; set; }
    public Uri? ServerUri;
    public IBrowser? Browser { get; private set; }
    public IPage? Page { get; set; }
    XUnitLogger Logger { get; }
    private string? CreatedUser;
    public string? Password { get; private set; }
    public bool IsAdmin { get; private set; }


    public PlaywrightTester(XUnitLogger logger, ServerTester? server = null)
    {
        Logger = logger;
        Server = server ?? new ServerTester("PlaywrightTest", logger);
    }

    public async Task StartAsync()
    {
        await Server.Start();
        var builder = new ConfigurationBuilder();
        builder.AddUserSecrets("AB0AC1DD-9D26-485B-9416-56A33F268117");
        var playwright = await Playwright.CreateAsync();
        Browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = false,
            SlowMo = 0 // 50 if you want to slow down
        });
        var context = await Browser.NewContextAsync();
        Page = await context.NewPageAsync();
        ServerUri = new Uri(Server.WebApp.Urls.FirstOrDefault() ?? throw new InvalidOperationException("No URLs found"));
        Logger.LogInformation($"Playwright: Using {Page.GetType()}");
        Logger.LogInformation($"Playwright: Browsing to {ServerUri}");
        await GoToLogin();
        await AssertNoError();
    }

    public async ValueTask DisposeAsync()
    {
        await SafeDispose(async () => await Page?.CloseAsync()!);
        await SafeDispose(async () => await Browser?.CloseAsync()!);
        await Server.DisposeAsync();
    }

    private static async Task SafeDispose(Func<Task> action)
    {
        try { if (action != null) await action(); }
        catch { }
    }

    public async Task AssertNoError()
    {
        if (Page is null)
            throw new InvalidOperationException("Page is not initialized");

        var pageSource = await Page.ContentAsync();
        if (pageSource.Contains("alert-danger"))
        {
            var dangerAlerts = Page.Locator(".alert-danger");
            var count = await dangerAlerts.CountAsync();
            for (var i = 0; i < count; i++)
            {
                var alert = dangerAlerts.Nth(i);
                if (!await alert.IsVisibleAsync()) continue;
                var alertText = await alert.InnerTextAsync();
                Assert.Fail($"No alert should be displayed, but found this on {Page.Url}: {alertText}");
            }
        }

        Assert.DoesNotContain("errors", Page.Url);
        var title = await Page.TitleAsync();
        Assert.DoesNotContain("Error", title, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<IResponse?> GoToUrl(string uri)
    {
        var fullUrl = new Uri(ServerUri ?? throw new InvalidOperationException(), uri).ToString();
        return await Page?.GotoAsync(fullUrl, new PageGotoOptions { WaitUntil = WaitUntilState.Commit })!;
    }

    public async Task GoToLogin()
    {
        await GoToUrl("/login");
    }
    public async Task Logout()
    {
        await Page?.Locator("#Nav-Account").ClickAsync()!;
        await Page.Locator("#Nav-Logout").ClickAsync();
    }


    public static string GetRandomUInt256()
    {
        var bytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    }

    public async Task<string> RegisterNewUser(bool isAdmin = false)
    {
        var usr = GetRandomUInt256()[(64 - 20)..] + "@a.com";
        await Page?.FillAsync("#Email", usr)!;
        await Page.FillAsync("#Password", "123456");
        await Page.FillAsync("#ConfirmPassword", "123456");
        if (isAdmin)
            await Page.ClickAsync("#IsAdmin");

        await Page.ClickAsync("#RegisterButton");
        CreatedUser = usr;
        Password = "123456";
        IsAdmin = isAdmin;
        return usr;
    }
    public async Task LogIn(string user, string password = "123456")
    {
        await GoToLogin();
        await Page?.FillAsync("#Email", user)!;
        await Page.FillAsync("#Password", password);
        await Page.ClickAsync("#LoginButton");
    }

    public async Task VerifyEmailAndGithubAsync(string email)
    {
        await using var scope = Server.WebApp.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<DBConnectionFactory>();
        await using var conn = await factory.Open();

        await conn.ExecuteAsync(
            """
            UPDATE "AspNetUsers"
                  SET "EmailConfirmed" = TRUE,
                      "GithubGistUrl" = 'https://gist.github.com/test-eligibility'
                  WHERE "Email" = @Email;
            """,
            new { Email = email });
    }
}
