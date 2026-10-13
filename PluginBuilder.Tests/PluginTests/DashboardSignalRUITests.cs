using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit;
using PluginBuilder.Events;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.PluginTests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class DashboardSignalRUITests(ITestOutputHelper output) : PageTest
{
    private readonly XUnitLogger _log = new("DashboardSignalRUITests", output);

    [Theory]
    [InlineData("/dashboard")]
    [InlineData("/plugins/{pluginSlug}")]
    [InlineData("/plugins/{pluginSlug}/builds/{buildId}")]
    public async Task Build_State_Updates_Through_The_Page_Hub(string pagePath)
    {
        var connections = new ConnectedHubFilter();
        await using var tester = new PlaywrightTester(_log) { Server = { ReuseDatabase = false } };
        tester.Server.ConfigureServices = services =>
        {
            new AdmissionTestSandbox().Register(services);
            services.AddSignalR(options => options.AddFilter(connections));
        };
        await tester.StartAsync();
        var email = $"dashboard-{Guid.NewGuid():N}@test.com";
        var ownerId = await tester.Server.CreateFakeUserAsync(email);
        var slug = ServerTester.CreatePluginSlug();
        await using var conn = await tester.Server.GetService<DBConnectionFactory>().Open();
        Assert.True(await conn.NewPlugin(slug, ownerId));
        var buildId = await conn.NewBuild(slug, new PluginBuildParameters(ServerTester.RepoUrl));
        var fullBuildId = new FullBuildId(slug, buildId);
        await tester.LogIn(email);
        // Login redirects to /dashboard; use a fresh tab so its negotiation cannot satisfy this assertion.
        var loginPage = Assert.IsAssignableFrom<IPage>(tester.Page);
        var page = await loginPage.Context.NewPageAsync();
        tester.Page = page;
        await loginPage.CloseAsync();
        pagePath = pagePath.Replace("{pluginSlug}", slug).Replace("{buildId}", buildId.ToString());

        // Exercise the URL constructed by the real Dashboard.js / Build.js, including negotiation.
        var negotiation = await page.RunAndWaitForResponseAsync(
            () => tester.GoToUrl(pagePath),
            response => new Uri(response.Url).AbsolutePath == $"{pagePath}/hub/negotiate");
        Assert.Equal(200, negotiation.Status);
        var negotiated = (await negotiation.JsonAsync())!.Value;
        await connections.WaitForConnection(negotiated.GetProperty("connectionId").GetString()!);

        var isBuildPage = pagePath.Contains("/builds/");
        var state = page.Locator(isBuildPage ? "#BuildState" : $"#plugin_{buildId} .badge");
        await Expect(state).ToHaveTextAsync("queued");
        await conn.UpdateBuild(fullBuildId, BuildStates.Running, null);
        tester.Server.GetService<EventAggregator>().Publish(new BuildChanged(fullBuildId, BuildStates.Running));
        await Expect(state).ToHaveTextAsync("running");
        if (isBuildPage)
            await Expect(page.Locator("#BuildProgressMessage")).ToHaveTextAsync("Build running...");
    }

    [Fact]
    public async Task Anonymous_Users_Cannot_Negotiate_Any_Hub()
    {
        await using var server = new ServerTester(nameof(Anonymous_Users_Cannot_Negotiate_Any_Hub), _log)
        {
            ReuseDatabase = false,
            ConfigureServices = services => new AdmissionTestSandbox().Register(services)
        };
        await server.Start();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(server.WebApp.Urls.First())
        };
        foreach (var path in new[] { "/hub", "/dashboard/hub", "/plugins/test/hub", "/plugins/test/builds/1/hub" })
        {
            using var response = await client.PostAsync($"{path}/negotiate?negotiateVersion=1", null);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    private sealed class ConnectedHubFilter : IHubFilter
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _connections = new();

        private TaskCompletionSource Connection(string id) =>
            _connections.GetOrAdd(id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        public Task WaitForConnection(string id) => Connection(id).Task.WaitAsync(TimeSpan.FromSeconds(10));

        public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
        {
            // The handshake precedes group membership; publish only after PluginHub has joined its groups.
            await next(context);
            Connection(context.Context.ConnectionId).TrySetResult();
        }
    }
}
