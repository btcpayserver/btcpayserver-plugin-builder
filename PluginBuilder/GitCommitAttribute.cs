namespace PluginBuilder;

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
