namespace PluginBuilder
{
    public class PluginBuildParameters
    {
        public PluginBuildParameters(string gitRepository)
        {
            GitRepository = gitRepository;
        }
        public string GitRepository { get; set; }
        public string? GitRef { get; set; }
        public string? PluginDirectory { get; set; }
        public string? BuildConfig { get; set; }
    }
}
