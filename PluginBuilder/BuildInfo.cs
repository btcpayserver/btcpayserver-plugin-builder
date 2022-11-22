#nullable disable
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PluginBuilder
{
    public class BuildInfo
    {

        public static BuildInfo Parse(string json)
        {
            return JsonConvert.DeserializeObject<BuildInfo>(json, CamelCaseSerializerSettings.Instance) ?? throw new FormatException("Invalid json for BuildInfo");
        }
        public string GitRepository { get; set; }
        public string GitRef { get; set; }
        public string PluginDir { get; set; }
        public string GitCommit { get; set; }
        public DateTimeOffset? GitCommitDate { get; set; }
        public DateTimeOffset? BuildDate { get; set; }
        public string BuildHash { get; set; }
        public string Url { get; set; }
        public string Error { get; set; }
        public string BuildConfig { get; set; }
        public string AssemblyName { get; set; }
        public IDictionary<string, JToken> AdditionalObjects { get; set; }
        public override string ToString()
        {
            return JsonConvert.SerializeObject(this, CamelCaseSerializerSettings.Instance);
        }
        public string ToString(Formatting formatting)
        {
            return JsonConvert.SerializeObject(this, formatting, CamelCaseSerializerSettings.Instance);
        }
    }
}
