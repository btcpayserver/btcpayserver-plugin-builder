namespace PluginBuilder.Services
{
    public class ServerEnvironment
    {
        public ServerEnvironment(IConfiguration configuration)
        {
            CheatMode = configuration.GetValue<bool?>("CHEAT_MODE") ?? false;
        }
        public bool CheatMode { get; set; }
    }
}
