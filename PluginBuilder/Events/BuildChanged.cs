namespace PluginBuilder.Events
{
    public class BuildChanged
    {
        public BuildChanged(FullBuildId fullBuildId)
        {
            FullBuildId = fullBuildId;
        }

        public FullBuildId FullBuildId { get; }
    }
}
