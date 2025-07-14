namespace PluginBuilder;

// used by csproj and other tools to get the git commit SHA
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class GitCommitAttribute : Attribute
{
    public GitCommitAttribute(string sha)
    {
        SHA = sha;
    }

    public string SHA
    {
        get;
    }
}
