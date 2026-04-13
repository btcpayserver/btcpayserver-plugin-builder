using PluginBuilder.APIModels;

namespace PluginBuilder.Services;

public interface IGitHostingProvider
{
    bool CanHandle(string repoUrl);
    Task<string> FetchIdentifierFromCsprojAsync(string repoUrl, string gitRef, string? pluginDir = null);
    Task<List<GitHubContributor>> GetContributorsAsync(string repoUrl, string pluginDir);
    string? GetSourceUrl(string repoUrl, string? commit, string? pluginDir);
    (string Owner, string RepoName)? ParseRepository(string repoUrl);
}
