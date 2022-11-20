namespace PluginBuilder
{
    [AttributeUsage(AttributeTargets.Assembly, Inherited = false)]
    public sealed class GitCommitAttribute : Attribute
    {
        public string SHA
        {
            get;
        }

        public GitCommitAttribute(string sha)
        {
            SHA = sha;
        }
    }
}
