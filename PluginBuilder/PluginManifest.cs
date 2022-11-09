using Newtonsoft.Json.Linq;

namespace PluginBuilder
{
    public class PluginManifest
    {
        public PluginManifest(JObject content)
        {
            Content = content;
            Version = TryParseVersion(content["Version"]);
            BTCPayMinVersion = TryParseMinBTCPayVersion(content);
        }

        private static int[]? TryParseMinBTCPayVersion(JObject manifest)
        {
            var version = manifest["Dependencies"]?.OfType<JObject>()
                .Where(o => o["Identifier"]?.Value<string>() == "BTCPayServer")
                .Select(o => o["Condition"])
                .FirstOrDefault()?
                .Value<string>();
            if (version is null || !version.StartsWith(">=", StringComparison.OrdinalIgnoreCase))
                return null;
            version = version.Substring(2);
            return TryParseVersion(new JValue(version));
        }

        private static int[]? TryParseVersion(JToken? version)
        {
            var parts = version?.Value<string>()?.Split('.');
            if (parts is null || parts.Length == 0)
                return null;
            int[] ret = new int[parts.Length];
            for (int i = 0; i < ret.Length; i++)
            {
                if (!int.TryParse(parts[i], out var v))
                    return null;
                ret[i] = v;
            }
            return ret;
        }

        public int[]? Version { get; }
        public int[]? BTCPayMinVersion { get; }

        public JObject Content { get; }

        public override string ToString()
        {
            return Content.ToString();
        }
    }
}
