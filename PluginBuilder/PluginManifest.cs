#nullable disable
using Newtonsoft.Json;
using PluginBuilder.JsonConverters;

namespace PluginBuilder;

public class PluginManifest
{
    public string Identifier { get; set; }
    public string Name { get; set; }

    [JsonConverter(typeof(PluginVersionConverter))]
    public PluginVersion Version { get; set; }

    public string Description { get; set; }
    public bool SystemPlugin { get; set; }
    public PluginDependency[] Dependencies { get; set; }


    [JsonIgnore]
    public PluginVersion BTCPayMinVersion { get; set; }

    public static PluginManifest Parse(string json)
    {
        var pluginManifest = JsonConvert.DeserializeObject<PluginManifest>(json) ??
                             throw new InvalidOperationException("Impossible to deserialized plugin manifest");
        if (pluginManifest.Version is null)
            throw new FormatException("Plugin's Version is missing");
        pluginManifest.BTCPayMinVersion = TryParseMinBTCPayVersion(pluginManifest);
        return pluginManifest;
    }

    private static PluginVersion TryParseMinBTCPayVersion(PluginManifest manifest)
    {
        var version = manifest.Dependencies?
            .Where(d => d.Identifier == "BTCPayServer")
            .Select(d => d.Condition).FirstOrDefault();
        if (version is null || !version.StartsWith(">=", StringComparison.OrdinalIgnoreCase))
            return null;
        version = version.Substring(2);
        PluginVersion.TryParse(version, out var s);
        return s;
    }

    public override string ToString()
    {
        return JsonConvert.SerializeObject(this);
    }

    public class PluginDependency
    {
        public string Identifier { get; set; }
        public string Condition { get; set; }

        public override string ToString()
        {
            return $"{Identifier}: {Condition}";
        }
    }
}
