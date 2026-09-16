using Xunit;

namespace PluginBuilder.Tests;

public class TestPathsTests
{
    [Fact]
    public void PhysicalTempDirectoryHasNoLinkedAncestors()
    {
        var path = TestPaths.GetPhysicalDirectoryPath(Path.GetTempPath());
        Assert.True(Directory.Exists(path));
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            Assert.Null(directory.LinkTarget);
    }

    [Fact]
    public void ResolvesLinkedAncestorAndLeafDirectories()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(TestPaths.GetPhysicalDirectoryPath(Path.GetTempPath()), "pb-test-paths-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "actual", "child"));
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "linked"), "actual");
            Assert.Equal(Path.Combine(root, "actual"), TestPaths.GetPhysicalDirectoryPath(Path.Combine(root, "linked")));
            Assert.Equal(Path.Combine(root, "actual", "child"), TestPaths.GetPhysicalDirectoryPath(Path.Combine(root, "linked", "child")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
