namespace PluginBuilder.Services
{
    public class ServerEnvironment
    {
        public ServerEnvironment(IConfiguration configuration)
        {
            CheatMode = configuration.GetValue<bool?>("CHEAT_MODE") ?? false;
            AdminAuthString = configuration.GetValue<string?>("ADMIN_AUTH_STRING") ?? null;
        }
        public bool CheatMode { get; set; }
        public string AdminAuthString { get; set; }
    }
}
