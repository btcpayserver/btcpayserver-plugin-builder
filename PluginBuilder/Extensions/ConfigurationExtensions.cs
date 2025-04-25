namespace PluginBuilder.Extensions;

public class ConfigurationRequiredException : ConfigurationException
{
    public ConfigurationRequiredException(string key) : base(key, "Required environment variable")
    {
    }
}

public class ConfigurationException : Exception
{
    public ConfigurationException(string key, string message) : base($"[{key}] {message}")
    {
        Key = key;
    }

    public string Key { get; }
}

public static class ConfigurationExtensions
{
    public static string GetRequired(this IConfiguration configuration, string key)
    {
        if (configuration[key] is string v && !string.IsNullOrWhiteSpace(v))
            return v;
        throw new ConfigurationRequiredException(key);
    }
}
