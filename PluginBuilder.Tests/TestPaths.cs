namespace PluginBuilder.Tests;

internal static class TestPaths
{
    // Resolve ancestors too: on macOS the default temp directory is below /var,
    // which is a symlink even though the temp directory itself is not.
    public static string GetPhysicalDirectoryPath(string path)
    {
        var directory = new DirectoryInfo(path);
        if (directory.Parent is null)
            return directory.FullName;

        var parent = GetPhysicalDirectoryPath(directory.Parent.FullName);
        directory = new DirectoryInfo(Path.Combine(parent, directory.Name));
        return directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? directory.FullName;
    }
}
