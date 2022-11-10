namespace PluginBuilder
{
    public class PluginVersion
    {
        public PluginVersion(int[] version)
        {
            Version = string.Join('.', version);
            VersionParts = version;
        }
        public string Version { get; }
        public int[] VersionParts { get; }
        public override string ToString()
        {
            return Version;
        }
    }
}
