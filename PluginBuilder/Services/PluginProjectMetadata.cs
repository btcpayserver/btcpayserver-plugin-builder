using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PluginBuilder.Services;

public record PluginProjectMetadata(string Identifier, string? BuildImage)
{
    public static PluginProjectMetadata Parse(string projectXml, string projectFileName)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(projectXml);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new BuildServiceException($"Failed to parse '{projectFileName}' as XML: {ex.Message}");
        }

        var identifier = document.Descendants("AssemblyName").FirstOrDefault()?.Value
                         ?? Path.GetFileNameWithoutExtension(projectFileName);
        var buildImage = document.Descendants("PluginBuildImage").FirstOrDefault()?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(buildImage))
            return new PluginProjectMetadata(identifier, null);

        if (!Regex.IsMatch(buildImage, @"^[^@\s]+@sha256:[a-f0-9]{64}$"))
            throw new BuildServiceException(
                $"PluginBuildImage in '{projectFileName}' must be an immutable repository@sha256 digest");

        return new PluginProjectMetadata(identifier, buildImage);
    }
}
