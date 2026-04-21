namespace PluginBuilder.Services;

public class GitHostingProviderFactory
{
    private readonly IEnumerable<IGitHostingProvider> _providers;

    public GitHostingProviderFactory(IEnumerable<IGitHostingProvider> providers)
    {
        _providers = providers;
    }

    public IGitHostingProvider? GetProvider(string? repoUrl)
    {
        if (string.IsNullOrWhiteSpace(repoUrl))
            return null;
        return _providers.FirstOrDefault(p => p.CanHandle(repoUrl));
    }
}
