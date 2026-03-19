#nullable disable
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using PluginBuilder.JsonConverters;

namespace PluginBuilder;

public class PluginManifest
{
    private static readonly Regex BTCPayVersionConditionRegex = new(
        @"^\s*>=\s*(?<min>\d+(?:\.\d+){0,3})\s*(?:&&\s*<=\s*(?<max>\d+(?:\.\d+){0,3})\s*)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public string Identifier { get; set; }
    public string Name { get; set; }

    [JsonConverter(typeof(PluginVersionConverter))]
    public PluginVersion Version { get; set; }

    public string Description { get; set; }
    public bool SystemPlugin { get; set; }
    public PluginDependency[] Dependencies { get; set; }


    [JsonIgnore]
    public PluginVersion BTCPayMinVersion { get; set; }

    [JsonIgnore]
    public PluginVersion BTCPayMaxVersion { get; set; }

    public static PluginManifest Parse(string json, bool strictBTCPayVersionCondition = false)
    {
        var pluginManifest = JsonConvert.DeserializeObject<PluginManifest>(json) ??
                             throw new InvalidOperationException("Impossible to deserialized plugin manifest");
        if (pluginManifest.Version is null)
            throw new FormatException("Plugin's Version is missing");
        var (minVersion, maxVersion) = TryParseBTCPayVersionRange(pluginManifest, strictBTCPayVersionCondition);
        pluginManifest.BTCPayMinVersion = minVersion;
        pluginManifest.BTCPayMaxVersion = maxVersion;
        return pluginManifest;
    }

    public static bool TryParse(string json, out PluginManifest manifest, bool strictBTCPayVersionCondition = false)
    {
        try
        {
            manifest = Parse(json, strictBTCPayVersionCondition);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
        {
            manifest = null!;
            return false;
        }
    }

    private static (PluginVersion minVersion, PluginVersion maxVersion) TryParseBTCPayVersionRange(PluginManifest manifest,
        bool strictBTCPayVersionCondition)
    {
        var btcpayDependencies = manifest.Dependencies?
            .Where(d => string.Equals(d.Identifier, "BTCPayServer", StringComparison.Ordinal))
            .ToArray() ?? [];
        if (btcpayDependencies.Length == 0)
            return (null, null);

        if (btcpayDependencies.Length > 1)
            return HandleInvalidCondition("Plugin manifest has multiple BTCPayServer dependency conditions");

        var condition = btcpayDependencies[0].Condition;
        if (string.IsNullOrWhiteSpace(condition))
            return HandleInvalidCondition("BTCPayServer dependency condition is missing");

        var match = BTCPayVersionConditionRegex.Match(condition);
        if (!match.Success)
            return HandleInvalidCondition("BTCPayServer dependency condition must be '>= min' or '>= min && <= max'");

        if (!PluginVersion.TryParse(match.Groups["min"].Value, out var minVersion))
            return HandleInvalidCondition("Invalid BTCPayServer minimum version condition");

        PluginVersion maxVersion = null;
        if (match.Groups["max"].Success)
        {
            if (!PluginVersion.TryParse(match.Groups["max"].Value, out maxVersion))
                return HandleInvalidCondition("Invalid BTCPayServer maximum version condition");

            if (maxVersion.CompareTo(minVersion) < 0)
                return HandleInvalidCondition("BTCPayServer maximum version must be greater than or equal to the minimum version");
        }

        return (minVersion, maxVersion);

        (PluginVersion minVersion, PluginVersion maxVersion) HandleInvalidCondition(string message)
        {
            return strictBTCPayVersionCondition ? throw new FormatException(message) : (null, null);
        }
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
