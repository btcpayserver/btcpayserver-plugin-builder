#nullable disable
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PluginBuilder
{
    public class BuildInfo
    {
        private static readonly JsonSerializerSettings CamelCase = new JsonSerializerSettings()
        {
            ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
            DefaultValueHandling = DefaultValueHandling.Ignore
        };

        public static BuildInfo Parse(string json)
        {
            return JsonConvert.DeserializeObject<BuildInfo>(json, CamelCase) ?? throw new FormatException("Invalid json for BuildInfo");
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
        public IDictionary<string, JToken> AdditionalObjects { get; set; }
        public override string ToString()
        {
            return JsonConvert.SerializeObject(this, CamelCase);
        }
        public string ToString(Formatting formatting)
        {
            return JsonConvert.SerializeObject(this, formatting, CamelCase);
        }
    }
}
