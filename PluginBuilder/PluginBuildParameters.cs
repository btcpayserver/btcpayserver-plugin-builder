namespace PluginBuilder
{
    public class PluginBuildParameters
    {
        public PluginBuildParameters(string gitRepository)
        {
            if (gitRepository is null)
                throw new ArgumentNullException(nameof(gitRepository));
            GitRepository = gitRepository;
        }
        public string GitRepository { get; set; }
        public string? GitRef { get; set; }
        public string? PluginDirectory { get; set; }
        public string? BuildConfig { get; set; }
    }
}
