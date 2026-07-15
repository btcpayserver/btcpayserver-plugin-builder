namespace PluginBuilder.Util;

/// <summary>
/// Host/ports for the local Mailpit dev instance published by PluginBuilder.Tests/docker-compose.yml.
/// Single source of truth shared by the cheat-mode "Use mailpit" button and the test harness.
/// </summary>
public static class MailpitDevSettings
{
    public const string Host = "127.0.0.1";
    public const int SmtpPort = 32829;
    public const int HttpPort = 32828;
}
