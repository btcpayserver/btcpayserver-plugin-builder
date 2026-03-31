using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using Dapper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using PluginBuilder.APIModels;
using PluginBuilder.Events;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;
using Microsoft.AspNetCore.Identity;
using PluginBuilder.Configuration;
using PluginBuilder.Util;

namespace PluginBuilder.Tests.ApiTests;

public class VersionLifecycleApiTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    private static readonly JsonSerializerSettings SerializerSettings = new() { ContractResolver = new CamelCasePropertyNamesContractResolver() };

    [Fact]
    public async Task CanListReleaseUnreleaseSignAndRemoveVersion()
    {
        await using var tester = Create();
        tester.ReuseDatabase = false;
        await tester.Start();

        var scenario = await CreateBuiltPluginScenarioAsync(tester);
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();

        var queuedBuildId = await conn.NewBuild(new PluginSlug(scenario.PluginSlug), new PluginBuildParameters(ServerTester.RepoUrl)
        {
            GitRef = ServerTester.GitRef,
            PluginDirectory = ServerTester.PluginDir
        });

        var listResponse = await scenario.Client.GetAsync($"/api/v1/plugins/{scenario.PluginSlug}/builds");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var builds = await ReadJsonArrayAsync(listResponse);
        Assert.True(builds.Count >= 2);
        Assert.Equal(queuedBuildId, builds[0]["buildId"]!.Value<long>());
        Assert.Equal(scenario.FullBuildId.BuildId, builds[1]["buildId"]!.Value<long>());
        Assert.All(builds.Take(2).Select(token => token["state"]?.Value<string>()), state => Assert.False(string.IsNullOrWhiteSpace(state)));

        var releaseResponse = await scenario.Client.PostAsync(
            $"/api/v1/plugins/{scenario.PluginSlug}/versions/{scenario.Version}/release",
            JsonBody(new { }));
        Assert.Equal(HttpStatusCode.OK, releaseResponse.StatusCode);
        Assert.False(await conn.ExecuteScalarAsync<bool>(
            "SELECT pre_release FROM versions WHERE plugin_slug=@pluginSlug AND ver=@version",
            new { pluginSlug = scenario.PluginSlug, version = PluginVersion.Parse(scenario.Version).VersionParts }));

        var unreleaseResponse = await scenario.Client.PostAsync(
            $"/api/v1/plugins/{scenario.PluginSlug}/versions/{scenario.Version}/unrelease",
            null);
        Assert.Equal(HttpStatusCode.OK, unreleaseResponse.StatusCode);
        Assert.True(await conn.ExecuteScalarAsync<bool>(
            "SELECT pre_release FROM versions WHERE plugin_slug=@pluginSlug AND ver=@version",
            new { pluginSlug = scenario.PluginSlug, version = PluginVersion.Parse(scenario.Version).VersionParts }));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var removedEventTask = tester.GetService<EventAggregator>().WaitNext<BuildChanged>(
            e => e.FullBuildId.Equals(scenario.FullBuildId) && e.EventName == BuildStates.Removed.ToEventName(),
            cts.Token);

        var removeResponse = await scenario.Client.DeleteAsync($"/api/v1/plugins/{scenario.PluginSlug}/versions/{scenario.Version}");
        Assert.Equal(HttpStatusCode.OK, removeResponse.StatusCode);
        await removedEventTask;

        Assert.Equal(
            BuildStates.Removed.ToEventName(),
            await conn.ExecuteScalarAsync<string>(
                "SELECT state FROM builds WHERE plugin_slug=@pluginSlug AND id=@buildId",
                new { pluginSlug = scenario.PluginSlug, buildId = scenario.FullBuildId.BuildId }));
        Assert.False(await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM versions WHERE plugin_slug=@pluginSlug AND ver=@version)",
            new { pluginSlug = scenario.PluginSlug, version = PluginVersion.Parse(scenario.Version).VersionParts }));
    }

    [Fact]
    public async Task ReleaseRejectsInvalidBuildState()
    {
        await using var tester = Create();
        tester.ReuseDatabase = false;
        await tester.Start();

        var scenario = await CreateBuiltPluginScenarioAsync(tester);
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.UpdateBuild(scenario.FullBuildId, BuildStates.Failed, null);

        var response = await scenario.Client.PostAsync(
            $"/api/v1/plugins/{scenario.PluginSlug}/versions/{scenario.Version}/release",
            JsonBody(new { }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var errors = await ReadErrorsAsync(response);
        Assert.Contains(errors, error => error.Path == string.Empty && error.Message.Contains("failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReleaseRejectsMissingAndInvalidSignatures()
    {
        await using var tester = Create();
        tester.ReuseDatabase = false;
        await tester.Start();

        var scenario = await CreateBuiltPluginScenarioAsync(tester);
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.SetPluginSettings(new PluginSlug(scenario.PluginSlug), new PluginSettings
        {
            RequireGPGSignatureForRelease = true
        });

        var missingSignatureResponse = await scenario.Client.PostAsync(
            $"/api/v1/plugins/{scenario.PluginSlug}/versions/{scenario.Version}/release",
            JsonBody(new { }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missingSignatureResponse.StatusCode);
        var missingErrors = await ReadErrorsAsync(missingSignatureResponse);
        Assert.Contains(missingErrors, error => error.Path == string.Empty && error.Message.Contains("required", StringComparison.OrdinalIgnoreCase));

        var invalidBase64Response = await scenario.Client.PostAsync(
            $"/api/v1/plugins/{scenario.PluginSlug}/versions/{scenario.Version}/release",
            JsonBody(new ReleaseVersionRequest { Signature = "not-base64" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidBase64Response.StatusCode);
        var invalidErrors = await ReadErrorsAsync(invalidBase64Response);
        Assert.Contains(invalidErrors, error => error.Path == nameof(ReleaseVersionRequest.Signature) && error.Message.Contains("base64", StringComparison.OrdinalIgnoreCase));
    }

    private static StringContent JsonBody(object body)
    {
        return new StringContent(JsonConvert.SerializeObject(body, SerializerSettings), Encoding.UTF8, "application/json");
    }

    private static async Task<JArray> ReadJsonArrayAsync(HttpResponseMessage response)
    {
        return JArray.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<ValidationError[]> ReadErrorsAsync(HttpResponseMessage response)
    {
        var body = JObject.Parse(await response.Content.ReadAsStringAsync());
        return body["errors"]?.ToObject<ValidationError[]>(JsonSerializer.Create(SerializerSettings)) ?? [];
    }

    private async Task<TestScenario> CreateBuiltPluginScenarioAsync(ServerTester tester)
    {
        var email = $"api-{Guid.NewGuid():N}@example.com";
        const string password = "123456";
        var ownerId = await tester.CreateFakeUserAsync(email, password);
        var pluginSlug = "api-vl-" + Guid.NewGuid().ToString("N")[..8];
        var fullBuildId = await tester.CreateAndBuildPluginAsync(ownerId, pluginSlug);

        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        
        var versionParts = await conn.QuerySingleAsync<int[]>(
            "SELECT ver FROM versions WHERE plugin_slug=@pluginSlug", 
            new { pluginSlug });

        var version = string.Join('.', versionParts);
        var client = tester.CreateHttpClient().SetBasicAuth(email, password);
        return new TestScenario(pluginSlug, version, fullBuildId, client);
    }

    private sealed record TestScenario(
        string PluginSlug,
        string Version,
        FullBuildId FullBuildId,
        HttpClient Client);

    [Fact]
    public async Task EmailPrimaryOwners_RedirectsToEmailSender_WithoutEmailsInQueryString()
    {
        await using var tester = Create();
        tester.ReuseDatabase = false;
        await tester.Start();

        // Create a plugin and release it as stable
        var scenario = await CreateBuiltPluginScenarioAsync(tester);
        var releaseResponse = await scenario.Client.PostAsync(
            $"/api/v1/plugins/{scenario.PluginSlug}/versions/{scenario.Version}/release",
            JsonBody(new { }));
        Assert.Equal(HttpStatusCode.OK, releaseResponse.StatusCode);

        // Create an admin user
        var adminEmail = $"admin-{Guid.NewGuid():N}@example.com";
        const string adminPassword = "123456";
        await tester.CreateFakeUserAsync(adminEmail, adminPassword);

        var roleManager = tester.GetService<RoleManager<IdentityRole>>();
        var userManager = tester.GetService<UserManager<IdentityUser>>();

        if (!await roleManager.RoleExistsAsync(Roles.ServerAdmin))
            await roleManager.CreateAsync(new IdentityRole(Roles.ServerAdmin));

        var adminUser = await userManager.FindByEmailAsync(adminEmail);
        await userManager.AddToRoleAsync(adminUser!, Roles.ServerAdmin);

        // Hit the endpoint as admin
        var adminClient = tester.CreateHttpClient().SetBasicAuth(adminEmail, adminPassword);
        var response = await adminClient.GetAsync("/admin/plugins/email-primary-owners");

        // Should redirect to EmailSender
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.ToString() ?? "";
        Assert.Contains("emailsender", location, StringComparison.OrdinalIgnoreCase);

        // Emails must NOT be in the query string
        Assert.DoesNotContain("to=", location, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmailPrimaryOwners_RedirectsToListPlugins_WhenNoStableVersionExists()
    {
        await using var tester = Create();
        tester.ReuseDatabase = false;
        await tester.Start();

        // Plugin exists but never released (still pre-release)
        var scenario = await CreateBuiltPluginScenarioAsync(tester);

        // Create an admin user
        var adminEmail = $"admin-{Guid.NewGuid():N}@example.com";
        const string adminPassword = "123456";
        await tester.CreateFakeUserAsync(adminEmail, adminPassword);

        var roleManager = tester.GetService<RoleManager<IdentityRole>>();
        var userManager = tester.GetService<UserManager<IdentityUser>>();

        if (!await roleManager.RoleExistsAsync(Roles.ServerAdmin))
            await roleManager.CreateAsync(new IdentityRole(Roles.ServerAdmin));

        var adminUser = await userManager.FindByEmailAsync(adminEmail);
        await userManager.AddToRoleAsync(adminUser!, Roles.ServerAdmin);

        // Hit the endpoint as admin
        var adminClient = tester.CreateHttpClient().SetBasicAuth(adminEmail, adminPassword);
        var response = await adminClient.GetAsync("/admin/plugins/email-primary-owners");

        // No stable versions exist, so should redirect back to ListPlugins
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.ToString() ?? "";
        Assert.Contains("listplugins", location, StringComparison.OrdinalIgnoreCase);
    }
}
