using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PluginBuilder.Util;

public static class ApplicationLogFiles
{
    public static FileStream OpenRead(FileInfo file)
    {
        var handle = OperatingSystem.IsWindows() ? OpenWindows(file.FullName) : OpenUnix(file.FullName);
        try
        {
            // Inspect the opened object, not cached metadata for a pathname that can be replaced.
            if ((File.GetAttributes(handle) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new FileNotFoundException("The application log is no longer available.");

            // Unix async reads are ThreadPool-backed; native descriptors are not marked async by SafeFileHandle.
            var stream = new FileStream(handle, FileAccess.Read, 4096, isAsync: OperatingSystem.IsWindows());
            if (!stream.CanSeek)
            {
                stream.Dispose();
                throw new IOException("The application log must be a seekable file.");
            }
            return stream;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenUnix(string path)
    {
        var linux = OperatingSystem.IsLinux();
        if (!linux && !OperatingSystem.IsMacOS())
            throw new IOException("Safe application log access is unavailable on this platform.");
        // Linux open flags differ on other architectures; never silently omit O_NOFOLLOW.
        if (linux && RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.X86 or Architecture.Arm64))
            throw new IOException("Safe application log access is unavailable on this architecture.");

        // O_RDONLY | O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK. NONBLOCK also prevents a substituted FIFO from hanging open.
        var flags = linux ? 0x20000 | 0x80000 | 0x800 : 0x100 | 0x1000000 | 0x4;
        int descriptor;
        int error;
        do
        {
            descriptor = NativeOpen(path, flags);
            error = Marshal.GetLastPInvokeError();
        } while (descriptor < 0 && error == 4); // EINTR
        if (descriptor < 0)
            throw OpenError(error, windows: false);

        // SafeFileHandle treats zero as invalid; close it rather than leak an unowned descriptor.
        if (descriptor == 0)
        {
            CloseDescriptor(0);
            throw new IOException("Could not acquire an application log handle.");
        }

        var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        // Match FileShare.ReadWrite | FileShare.Delete's best-effort shared lock on Unix.
        int locked;
        do
        {
            locked = LockDescriptor(handle, 1 | 4); // LOCK_SH | LOCK_NB
            error = Marshal.GetLastPInvokeError();
        } while (locked < 0 && error == 4);
        if (locked < 0 && error == (linux ? 11 : 35)) // EWOULDBLOCK
        {
            handle.Dispose();
            throw new IOException("The application log is in use.");
        }
        return handle;
    }

    private static SafeFileHandle OpenWindows(string path)
    {
        // Preserve long local and UNC paths when calling Win32 directly.
        if (!path.StartsWith(@"\\?\", StringComparison.Ordinal))
            path = path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

        var handle = CreateFile(path, 0x80000000, FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero, 3, // GENERIC_READ, OPEN_EXISTING
            (uint)(FileOptions.Asynchronous | FileOptions.RandomAccess) | 0x00200000, // FILE_FLAG_OPEN_REPARSE_POINT
            IntPtr.Zero);
        if (!handle.IsInvalid)
            return handle;
        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw OpenError(error, windows: true);
    }

    private static Exception OpenError(int error, bool windows)
    {
        if (windows ? error is 2 or 3 : error is 2 or 20 || error == (OperatingSystem.IsLinux() ? 40 : 62))
            return new FileNotFoundException("The application log is no longer available.");
        if (windows ? error == 5 : error is 1 or 13)
            return new UnauthorizedAccessException("The application log is not accessible.");
        return new IOException("Could not open the application log.", new Win32Exception(error));
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int NativeOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "close")]
    private static extern int CloseDescriptor(int descriptor);

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int LockDescriptor(SafeFileHandle handle, int operation);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security,
        uint creation, uint flags, IntPtr template);

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
