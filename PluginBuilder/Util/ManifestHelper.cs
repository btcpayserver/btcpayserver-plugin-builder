using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PluginBuilder.Util;

public static class ManifestHelper
{
    public static string? NiceJson(string? json, string? fingerprint = null)
    {
        if (json is null)
            return null;
        var data = JObject.Parse(json);
        data = new JObject(data.Properties().OrderBy(p => p.Name));
        if (!string.IsNullOrWhiteSpace(fingerprint))
            data["SignatureFingerprint"] = fingerprint;
        return data.ToString(Formatting.Indented);
    }

    public static string GetManifestHash(string? manifestInfo, bool requiresGPGSignature)
    {
        if (!requiresGPGSignature || string.IsNullOrEmpty(manifestInfo))
            return string.Empty;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(manifestInfo));
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }
}
