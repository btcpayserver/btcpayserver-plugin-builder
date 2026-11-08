namespace PluginBuilder.Util;

public static class ApplicationLogFiles
{
    public static List<FileInfo> GetFiles(string? configuredFile)
    {
        if (string.IsNullOrEmpty(configuredFile))
            return [];

        var directory = Directory.GetParent(configuredFile);
        if (directory is null)
            return [];

        var name = Path.GetFileNameWithoutExtension(configuredFile);
        var extension = Path.GetExtension(configuredFile);
        try
        {
            return directory.GetFiles($"{name}*{extension}")
                .Where(file => (file.Attributes & FileAttributes.ReparsePoint) == 0)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenBy(file => file.Name, StringComparer.Ordinal)
                .ToList();
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
    }
}
