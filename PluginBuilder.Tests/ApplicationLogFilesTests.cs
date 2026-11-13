using System.Text;
using PluginBuilder.Util;
using Xunit;

namespace PluginBuilder.Tests;

public class ApplicationLogFilesTests
{
    [SymbolicLinkFact]
    public void RejectsASelectedLogReplacedByASymbolicLink()
    {
        using var files = new LogFiles();
        File.WriteAllText(files.Log, "Fictional application log");
        File.WriteAllText(files.Outside, "Fictional file outside the log directory");
        var selected = Assert.Single(ApplicationLogFiles.GetFiles(files.Log));
        Assert.Equal(0, (int)(selected.Attributes & FileAttributes.ReparsePoint));

        File.Delete(files.Log);
        File.CreateSymbolicLink(files.Log, files.Outside);

        Assert.Throws<FileNotFoundException>(() =>
        {
            using var stream = ApplicationLogFiles.OpenRead(selected);
        });
    }

    [SymbolicLinkFact]
    public void RejectsAnExistingSymbolicLink()
    {
        using var files = new LogFiles();
        File.WriteAllText(files.Outside, "Fictional file outside the log directory");
        File.CreateSymbolicLink(files.Log, files.Outside);

        Assert.Throws<FileNotFoundException>(() =>
        {
            using var stream = ApplicationLogFiles.OpenRead(new FileInfo(files.Log));
        });
    }

    [Fact]
    public void RespectsAnExclusiveWriter()
    {
        using var files = new LogFiles();
        File.WriteAllText(files.Log, "Fictional application log");
        using var writer = new FileStream(files.Log, FileMode.Open, FileAccess.Write, FileShare.None);

        Assert.Throws<IOException>(() =>
        {
            using var stream = ApplicationLogFiles.OpenRead(new FileInfo(files.Log));
        });
    }

    [Fact]
    public void ReportsAMissingLogAsNotFound()
    {
        using var files = new LogFiles();

        Assert.Throws<FileNotFoundException>(() =>
        {
            using var stream = ApplicationLogFiles.OpenRead(new FileInfo(files.Log));
        });
    }

    [Fact]
    public async Task ReadsAsynchronouslyWhileTheLogWriterRemainsOpen()
    {
        using var files = new LogFiles();
        const string first = "Fictional application log: ação 🔧\n";
        const string appended = "Fictional appended output\n";
        await File.WriteAllTextAsync(files.Log, first, new UTF8Encoding(false));
        await using var writer = new FileStream(files.Log, FileMode.Open, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        await using var stream = ApplicationLogFiles.OpenRead(new FileInfo(files.Log));

        writer.Position = writer.Length;
        await writer.WriteAsync(Encoding.UTF8.GetBytes(appended));
        await writer.FlushAsync();

        using var reader = new StreamReader(stream);
        Assert.Equal(first + appended, await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task KeepsTheOpenedLogReadableAfterRotationAndDeletion()
    {
        using var files = new LogFiles();
        const string original = "Fictional original log\n";
        const string replacement = "Fictional replacement log\n";
        await File.WriteAllTextAsync(files.Log, original);
        var selected = Assert.Single(ApplicationLogFiles.GetFiles(files.Log));
        await using var stream = ApplicationLogFiles.OpenRead(selected);

        var rotated = Path.Combine(files.Directory, "application-old.log");
        File.Move(files.Log, rotated);
        await File.WriteAllTextAsync(files.Log, replacement);
        File.Delete(rotated);

        using var reader = new StreamReader(stream);
        Assert.Equal(original, await reader.ReadToEndAsync());
        await using var current = ApplicationLogFiles.OpenRead(new FileInfo(files.Log));
        using var currentReader = new StreamReader(current);
        Assert.Equal(replacement, await currentReader.ReadToEndAsync());
    }

    private sealed class LogFiles : IDisposable
    {
        private readonly string root = System.IO.Directory.CreateTempSubdirectory("pb-log-open-tests-").FullName;

        public LogFiles() => System.IO.Directory.CreateDirectory(Directory);

        public string Directory => Path.Combine(root, "logs");
        public string Log => Path.Combine(Directory, "application.log");
        public string Outside => Path.Combine(root, "fictional-outside.txt");

        public void Dispose() => System.IO.Directory.Delete(root, true);
    }
}
