using Newtonsoft.Json;

namespace PluginBuilder.JsonConverters;

public static class SafeJson
{
    public static T Deserialize<T>(string json) where T : new()
    {
        try
        {
            return JsonConvert.DeserializeObject<T>(json) ?? new T();
        }
        catch (JsonException)
        {
            return new T();
        }
    }
}
