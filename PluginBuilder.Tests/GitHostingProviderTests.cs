using Newtonsoft.Json.Linq;
using PluginBuilder.APIModels;
using PluginBuilder.Services;
using PluginBuilder.Util;
using Xunit;

namespace PluginBuilder.Tests;

public class GitHostingProviderTests
{
    // ── GitHub CanHandle ──────────────────────────────────────────────

    [Theory]
    [InlineData("https://github.com/owner/repo", true)]
    [InlineData("https://www.github.com/owner/repo", true)]
    [InlineData("https://github.com/owner/repo.git", true)]
    [InlineData("https://GITHUB.COM/owner/repo", true)]
    [InlineData("https://gitlab.com/owner/repo", false)]
    [InlineData("https://bitbucket.org/owner/repo", false)]
    [InlineData("not a url", false)]
    public void GitHub_CanHandle(string url, bool expected)
    {
        var provider = CreateGitHubProvider();
        Assert.Equal(expected, provider.CanHandle(url));
    }

    // ── GitLab CanHandle ──────────────────────────────────────────────

    [Theory]
    [InlineData("https://gitlab.com/owner/repo", true)]
    [InlineData("https://www.gitlab.com/owner/repo", true)]
    [InlineData("https://gitlab.com/group/subgroup/repo", true)]
    [InlineData("https://GITLAB.COM/owner/repo", true)]
    [InlineData("https://github.com/owner/repo", false)]
    [InlineData("not a url", false)]
    public void GitLab_CanHandle(string url, bool expected)
    {
        var provider = CreateGitLabProvider();
        Assert.Equal(expected, provider.CanHandle(url));
    }

    [Theory]
    [InlineData("https://gitlab.selfhosted.com/owner/repo", true)]
    [InlineData("https://github.com/owner/repo", false)]
    public void GitLab_CanHandle_AdditionalHosts(string url, bool expected)
    {
        var provider = CreateGitLabProvider(additionalHosts: new[] { "gitlab.selfhosted.com" });
        Assert.Equal(expected, provider.CanHandle(url));
    }

    // ── GitHub ParseRepository ────────────────────────────────────────

    [Theory]
    [InlineData("https://github.com/Kukks/btcpayserver", "Kukks", "btcpayserver")]
    [InlineData("https://github.com/Kukks/btcpayserver.git", "Kukks", "btcpayserver")]
    [InlineData("https://www.github.com/Kukks/btcpayserver/", "Kukks", "btcpayserver")]
    public void GitHub_ParseRepository_Valid(string url, string expectedOwner, string expectedRepo)
    {
        var provider = CreateGitHubProvider();
        var result = provider.ParseRepository(url);
        Assert.NotNull(result);
        Assert.Equal(expectedOwner, result.Value.Owner);
        Assert.Equal(expectedRepo, result.Value.RepoName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://github.com/")]
    [InlineData("https://gitlab.com/owner/repo")]
    public void GitHub_ParseRepository_Invalid(string url)
    {
        var provider = CreateGitHubProvider();
        var result = provider.ParseRepository(url);
        Assert.Null(result);
    }

    // ── GitLab ParseRepository ────────────────────────────────────────

    [Theory]
    [InlineData("https://gitlab.com/owner/repo", "owner", "repo")]
    [InlineData("https://gitlab.com/owner/repo.git", "owner", "repo")]
    [InlineData("https://gitlab.com/group/subgroup/repo", "group/subgroup", "repo")]
    [InlineData("https://gitlab.com/a/b/c/repo", "a/b/c", "repo")]
    public void GitLab_ParseRepository_Valid(string url, string expectedOwner, string expectedRepo)
    {
        var provider = CreateGitLabProvider();
        var result = provider.ParseRepository(url);
        Assert.NotNull(result);
        Assert.Equal(expectedOwner, result.Value.Owner);
        Assert.Equal(expectedRepo, result.Value.RepoName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://gitlab.com/")]
    [InlineData("https://gitlab.com/onlyone")]
    public void GitLab_ParseRepository_Invalid(string url)
    {
        var provider = CreateGitLabProvider();
        var result = provider.ParseRepository(url);
        Assert.Null(result);
    }

    // ── GitHub GetSourceUrl ───────────────────────────────────────────

    [Fact]
    public void GitHub_GetSourceUrl_WithCommitAndPluginDir()
    {
        var provider = CreateGitHubProvider();
        var url = provider.GetSourceUrl(
            "https://github.com/NicolasDorier/btcpayserver",
            "abc123",
            "Plugins/MyPlugin");
        Assert.Equal("https://github.com/NicolasDorier/btcpayserver/tree/abc123/Plugins/MyPlugin", url);
    }

    [Fact]
    public void GitHub_GetSourceUrl_WithCommitNoPluginDir()
    {
        var provider = CreateGitHubProvider();
        var url = provider.GetSourceUrl(
            "https://github.com/NicolasDorier/btcpayserver",
            "abc123",
            null);
        Assert.Equal("https://github.com/NicolasDorier/btcpayserver/tree/abc123", url);
    }

    [Fact]
    public void GitHub_GetSourceUrl_NullCommit()
    {
        var provider = CreateGitHubProvider();
        var url = provider.GetSourceUrl(
            "https://github.com/NicolasDorier/btcpayserver",
            null,
            "Plugins/MyPlugin");
        Assert.Null(url);
    }

    [Fact]
    public void GitHub_GetSourceUrl_GitAtUrl()
    {
        var provider = CreateGitHubProvider();
        var url = provider.GetSourceUrl(
            "git@github.com:Kukks/btcpayserver.git",
            "abc123",
            "Plugins/AOPP");
        Assert.Equal("https://github.com/Kukks/btcpayserver/tree/abc123/Plugins/AOPP", url);
    }

    // ── GitLab GetSourceUrl ───────────────────────────────────────────

    [Fact]
    public void GitLab_GetSourceUrl_WithCommitAndPluginDir()
    {
        var provider = CreateGitLabProvider();
        var url = provider.GetSourceUrl(
            "https://gitlab.com/mygroup/myrepo",
            "def456",
            "src/MyPlugin");
        Assert.Equal("https://gitlab.com/mygroup/myrepo/-/tree/def456/src/MyPlugin", url);
    }

    [Fact]
    public void GitLab_GetSourceUrl_NestedGroup()
    {
        var provider = CreateGitLabProvider();
        var url = provider.GetSourceUrl(
            "https://gitlab.com/group/subgroup/repo",
            "def456",
            null);
        Assert.Equal("https://gitlab.com/group/subgroup/repo/-/tree/def456", url);
    }

    [Fact]
    public void GitLab_GetSourceUrl_NullCommit()
    {
        var provider = CreateGitLabProvider();
        var url = provider.GetSourceUrl(
            "https://gitlab.com/owner/repo",
            null,
            null);
        Assert.Null(url);
    }

    [Fact]
    public void GitLab_GetSourceUrl_DotGitSuffix()
    {
        var provider = CreateGitLabProvider();
        var url = provider.GetSourceUrl(
            "https://gitlab.com/owner/repo.git",
            "abc",
            null);
        Assert.Equal("https://gitlab.com/owner/repo/-/tree/abc", url);
    }

    // ── Factory ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://github.com/owner/repo", typeof(GitHubHostingProvider))]
    [InlineData("https://gitlab.com/owner/repo", typeof(GitLabHostingProvider))]
    public void Factory_ReturnsCorrectProvider(string url, Type expectedType)
    {
        var factory = CreateFactory();
        var provider = factory.GetProvider(url);
        Assert.NotNull(provider);
        Assert.IsType(expectedType, provider);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://bitbucket.org/owner/repo")]
    public void Factory_ReturnsNull_ForUnsupported(string? url)
    {
        var factory = CreateFactory();
        var provider = factory.GetProvider(url);
        Assert.Null(provider);
    }

    // ── PublishedPlugin provider-agnostic methods ─────────────────────

    [Fact]
    public void PublishedPlugin_GetSourceUrl_GitHub()
    {
        var factory = CreateFactory();
        var plugin = new PublishedPlugin
        {
            BuildInfo = JObject.FromObject(new
            {
                gitRepository = "https://github.com/owner/repo",
                gitCommit = "abc123",
                pluginDir = "Plugins/MyPlugin"
            })
        };
        var url = plugin.GetSourceUrl(factory);
        Assert.Equal("https://github.com/owner/repo/tree/abc123/Plugins/MyPlugin", url);
    }

    [Fact]
    public void PublishedPlugin_GetSourceUrl_GitLab()
    {
        var factory = CreateFactory();
        var plugin = new PublishedPlugin
        {
            BuildInfo = JObject.FromObject(new
            {
                gitRepository = "https://gitlab.com/group/repo",
                gitCommit = "def456",
                pluginDir = "src/Plugin"
            })
        };
        var url = plugin.GetSourceUrl(factory);
        Assert.Equal("https://gitlab.com/group/repo/-/tree/def456/src/Plugin", url);
    }

    [Fact]
    public void PublishedPlugin_GetOwnerName_GitHub()
    {
        var factory = CreateFactory();
        var plugin = new PublishedPlugin
        {
            BuildInfo = JObject.FromObject(new
            {
                gitRepository = "https://github.com/NicolasDorier/btcpayserver"
            })
        };
        Assert.Equal("NicolasDorier", plugin.GetOwnerName(factory));
    }

    [Fact]
    public void PublishedPlugin_GetOwnerName_GitLab_NestedGroup()
    {
        var factory = CreateFactory();
        var plugin = new PublishedPlugin
        {
            BuildInfo = JObject.FromObject(new
            {
                gitRepository = "https://gitlab.com/group/subgroup/repo"
            })
        };
        Assert.Equal("group/subgroup", plugin.GetOwnerName(factory));
    }

    [Fact]
    public void PublishedPlugin_GetOwnerProfileUrl_GitHub()
    {
        var factory = CreateFactory();
        var plugin = new PublishedPlugin
        {
            BuildInfo = JObject.FromObject(new
            {
                gitRepository = "https://github.com/Kukks/btcpayserver"
            })
        };
        Assert.Equal("https://github.com/Kukks", plugin.GetOwnerProfileUrl(factory));
    }

    [Fact]
    public void PublishedPlugin_GetOwnerProfileUrl_GitLab()
    {
        var factory = CreateFactory();
        var plugin = new PublishedPlugin
        {
            BuildInfo = JObject.FromObject(new
            {
                gitRepository = "https://gitlab.com/mygroup/myrepo"
            })
        };
        Assert.Equal("https://gitlab.com/mygroup", plugin.GetOwnerProfileUrl(factory));
    }

    [Fact]
    public void PublishedPlugin_GetSourceUrl_NullBuildInfo()
    {
        var factory = CreateFactory();
        var plugin = new PublishedPlugin { BuildInfo = null };
        Assert.Null(plugin.GetSourceUrl(factory));
    }

    // ── PluginController.GetUrl backward compatibility ────────────────

    [Fact]
    public void PluginController_GetUrl_GitHub_WithFactory()
    {
        var factory = CreateFactory();
        var buildInfo = new BuildInfo
        {
            GitRepository = "https://github.com/Kukks/btcpayserver",
            GitCommit = "abc123",
            PluginDir = "Plugins/AOPP"
        };
        var url = Controllers.PluginController.GetUrl(buildInfo, factory);
        Assert.Equal("https://github.com/Kukks/btcpayserver/tree/abc123/Plugins/AOPP", url);
    }

    [Fact]
    public void PluginController_GetUrl_GitLab_WithFactory()
    {
        var factory = CreateFactory();
        var buildInfo = new BuildInfo
        {
            GitRepository = "https://gitlab.com/group/repo",
            GitCommit = "def456",
            PluginDir = "src/Plugin"
        };
        var url = Controllers.PluginController.GetUrl(buildInfo, factory);
        Assert.Equal("https://gitlab.com/group/repo/-/tree/def456/src/Plugin", url);
    }

    [Fact]
    public void PluginController_GetUrl_GitAtUrl_WithoutFactory()
    {
        // Backward compat: no factory, git@ URL should still work
        var buildInfo = new BuildInfo
        {
            GitRepository = "git@github.com:Kukks/btcpayserver.git",
            GitCommit = "abc123",
            PluginDir = "Plugins/AOPP"
        };
        var url = Controllers.PluginController.GetUrl(buildInfo);
        Assert.Equal("https://github.com/Kukks/btcpayserver/tree/abc123/Plugins/AOPP", url);
    }

    [Fact]
    public void PluginController_GetUrl_NullBuildInfo()
    {
        var factory = CreateFactory();
        Assert.Null(Controllers.PluginController.GetUrl(null, factory));
    }

    [Fact]
    public void PluginController_GetUrl_UnsupportedProvider_WithoutFactory()
    {
        var buildInfo = new BuildInfo
        {
            GitRepository = "https://bitbucket.org/owner/repo",
            GitCommit = "abc123"
        };
        Assert.Null(Controllers.PluginController.GetUrl(buildInfo));
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static GitHubHostingProvider CreateGitHubProvider()
    {
        var factory = new TestHttpClientFactory();
        return new GitHubHostingProvider(factory);
    }

    private static GitLabHostingProvider CreateGitLabProvider(IEnumerable<string>? additionalHosts = null)
    {
        var factory = new TestHttpClientFactory();
        return new GitLabHostingProvider(factory, additionalHosts);
    }

    private static GitHostingProviderFactory CreateFactory()
    {
        var httpFactory = new TestHttpClientFactory();
        var providers = new IGitHostingProvider[]
        {
            new GitHubHostingProvider(httpFactory),
            new GitLabHostingProvider(httpFactory)
        };
        return new GitHostingProviderFactory(providers);
    }

    private class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
