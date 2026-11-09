using Xunit;

namespace PluginBuilder.Tests;

public sealed class SymbolicLinkFactAttribute : FactAttribute
{
    public SymbolicLinkFactAttribute()
    {
        var directory = Directory.CreateTempSubdirectory("pb-symlink-capability-").FullName;
        try
        {
            var target = Path.Combine(directory, "target.txt");
            File.WriteAllText(target, "Symbolic-link test capability probe");
            try
            {
                File.CreateSymbolicLink(Path.Combine(directory, "link.txt"), target);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException ||
                                             (OperatingSystem.IsWindows() && exception is IOException &&
                                              (exception.HResult & 0xffff) is 1314 or 50))
            {
                Skip = "Symbolic-link creation is unavailable or requires additional privileges.";
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
