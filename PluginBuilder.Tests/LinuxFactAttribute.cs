using Xunit;

namespace PluginBuilder.Tests;

public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "Requires Linux kernel interfaces.";
    }
}
