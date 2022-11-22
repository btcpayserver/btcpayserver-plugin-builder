using Newtonsoft.Json;

namespace PluginBuilder
{
    public class CamelCaseSerializerSettings
    {
        public static readonly JsonSerializerSettings Instance = new JsonSerializerSettings()
        {
            ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
            DefaultValueHandling = DefaultValueHandling.Ignore
        };

    }
}
