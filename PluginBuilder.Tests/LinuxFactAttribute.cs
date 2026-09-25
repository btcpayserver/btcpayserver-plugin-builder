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

// Tests that drive shell scripts or Unix file modes: skipped, not passed, on Windows.
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "Requires a Unix shell and file modes.";
    }
}

public sealed class UnixTheoryAttribute : TheoryAttribute
{
    public UnixTheoryAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "Requires a Unix shell and file modes.";
    }
}
