using Newtonsoft.Json;

namespace PluginBuilder.JsonConverters;

public class PluginVersionConverter : JsonConverter<PluginVersion>
{
    public override PluginVersion? ReadJson(JsonReader reader, Type objectType, PluginVersion? existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        if (reader.Value is not string v)
            return null;
        return PluginVersion.Parse(v);
    }

    public override void WriteJson(JsonWriter writer, PluginVersion? value, JsonSerializer serializer)
    {
        if (value is not null)
            writer.WriteValue(value.ToString());
    }
}
