using Newtonsoft.Json;

namespace PluginBuilder.APIModels;

public sealed record InstalledPluginRequest(string Identifier, string Version);
