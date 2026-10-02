using PluginBuilder.APIModels;
using PluginBuilder.Services;
using Xunit;

namespace PluginBuilder.Tests;

public class GithubServiceTests
{
    [Fact]
    public async Task SnapshotPreservesContributors()
    {
        var directory = Directory.CreateTempSubdirectory("pb-contributor-snapshot-");
        try
        {
            var pluginDataDir = Path.Combine(directory.FullName, "plugin-data");
            var pluginSlug = new PluginSlug("snapshot-test");
            List<GitHubContributor> contributors =
            [
                new()
                {
                    Login = "alice",
                    AvatarUrl = "https://example.com/alice.png",
                    HtmlUrl = "https://github.com/alice",
                    UserViewType = "public",
                    Contributions = 12
                },
                new()
                {
                    Login = "bob",
                    AvatarUrl = "https://example.com/bob.png",
                    HtmlUrl = "https://github.com/bob",
                    UserViewType = "public",
                    Contributions = 3
                }
            ];

            await GithubService.SaveSnapshot(pluginDataDir, pluginSlug, contributors);
            var loaded = GithubService.LoadSnapshot(pluginDataDir, pluginSlug);

            Assert.Equal(
                contributors.Select(c => (c.Login, c.AvatarUrl, c.HtmlUrl, c.UserViewType, c.Contributions)),
                loaded.Select(c => (c.Login, c.AvatarUrl, c.HtmlUrl, c.UserViewType, c.Contributions)));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
