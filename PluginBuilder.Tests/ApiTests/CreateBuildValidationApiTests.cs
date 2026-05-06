using System.Net;
using System.Text;
using Dapper;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using PluginBuilder.APIModels;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.ApiTests;

public class CreateBuildValidationApiTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    private const string Password = "123456";
    private const string MediaType = "application/json";

    private static readonly JsonSerializerSettings SerializerSettings = new() { ContractResolver = new CamelCasePropertyNamesContractResolver() };

    [Fact]
    public async Task Should_Return422_If_InvalidPluginDirectory_WithoutCsproj()
    {
        // Arrange
        await using var tester = Create();
        tester.ReuseDatabase = false;
        await tester.Start();

        var email = $"test-{Guid.NewGuid():N}@example.com";
        var ownerId = await tester.CreateFakeUserAsync(email, Password);
        var pluginSlug = "test-no-csproj-" + Guid.NewGuid().ToString("N")[..8];

        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.NewPlugin(pluginSlug, ownerId);

        var client = tester.CreateHttpClient().SetBasicAuth(email, Password);

        var request = new CreateBuildRequest
        {
            GitRepository = ServerTester.RepoUrl,
            GitRef = ServerTester.GitRef,
            PluginDirectory = "Invalid/Path/Without/Csproj",
            BuildConfig = "Release"
        };

        // Act
        var content = new StringContent(
            JsonConvert.SerializeObject(request, SerializerSettings),
            Encoding.UTF8,
            MediaType);

        var response = await client.PostAsync(
            $"/api/v1/plugins/{pluginSlug}/builds",
            content);

        // Assert
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var result = await response.Content.ReadAsStringAsync();
        Assert.Contains("Manifest validation failed:", result);

        Assert.False(await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM builds WHERE plugin_slug = @pluginSlug)",
            new { pluginSlug }));
    }

    [Fact]
    public async Task Should_Return422_If_SameRepo_UsedForAnotherPluginSlug()
    {
        // Arrange
        await using var tester = Create();
        tester.ReuseDatabase = false;
        await tester.Start();

        var email1 = $"owner1-{Guid.NewGuid():N}@example.com";
        var email2 = $"owner2-{Guid.NewGuid():N}@example.com";

        var ownerId1 = await tester.CreateFakeUserAsync(email1, Password);
        var ownerId2 = await tester.CreateFakeUserAsync(email2, Password);
        var pluginSlug1 = "first-plugin-" + Guid.NewGuid().ToString("N")[..8];

        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        await conn.NewPlugin(pluginSlug1, ownerId1);

        var buildService = tester.GetService<BuildService>();
        var actualIdentifier = await buildService.FetchIdentifierFromCsprojAsync(
            ServerTester.RepoUrl,
            ServerTester.GitRef,
            ServerTester.PluginDir);

        await conn.ExecuteAsync(
            "UPDATE plugins SET identifier = @identifier WHERE slug = @slug",
            new
            {
                identifier = actualIdentifier,
                slug = pluginSlug1
            });

        var client = tester.CreateHttpClient().SetBasicAuth(email2, Password);

        var pluginSlug2 = "second-plugin-" + Guid.NewGuid().ToString("N")[..8];
        await conn.NewPlugin(pluginSlug2, ownerId2);

        var request = new CreateBuildRequest
        {
            GitRepository = ServerTester.RepoUrl,
            GitRef = ServerTester.GitRef,
            PluginDirectory = ServerTester.PluginDir,
            BuildConfig = "Release"
        };

        // Act
        var content = new StringContent(
            JsonConvert.SerializeObject(request, SerializerSettings),
            Encoding.UTF8,
            MediaType);

        var response = await client.PostAsync(
            $"/api/v1/plugins/{pluginSlug2}/builds",
            content);

        // Assert
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var result = await response.Content.ReadAsStringAsync();
        Assert.Contains("does not belong to plugin slug", result);

        Assert.False(await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM builds WHERE plugin_slug = @pluginSlug)",
            new { pluginSlug = pluginSlug2 }));
    }
}
