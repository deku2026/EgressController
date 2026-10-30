using System.Runtime.InteropServices;
using EgressController.Mac;

namespace EgressController.MacHelper;

internal static partial class HostFiles
{
    public static byte[] ReadOwned(NativeMacPrivileges native, string path, uint owner, int maximum)
    {
        using var handle = native.OpenOwnedRead(path, owner, maximum);
        using var stream = new FileStream(handle, FileAccess.Read);
        using var output = new MemoryStream(); var buffer = new byte[65536];
        while (true) { int count = stream.Read(buffer); if (count == 0) break; if (output.Length + count > maximum) throw new IOException("Private file exceeded its bound."); output.Write(buffer, 0, count); }
        return output.ToArray();
    }
    public static void PrivateDirectory(NativeMacPrivileges native, string path, bool restrictToRoot = true)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        if (!Path.GetFullPath(path).StartsWith("/Library/", StringComparison.Ordinal)) throw new IOException("Unexpected helper system directory.");
        string parent = "/";
        foreach (string segment in Path.GetFullPath(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            parent = Path.Combine(parent, segment);
            if (!Directory.Exists(parent)) Directory.CreateDirectory(parent, restrictToRoot
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            var info = native.ReadFileInfo(parent);
            if (!info.IsDirectory || info.IsSymbolicLink || info.UserId != 0 || (info.Mode & 0x12) != 0)
                throw new IOException("Helper directory ancestors must be root-owned, non-writable and non-symbolic.");
        }
        if (restrictToRoot) native.SetOwnerAndMode(path, 0, 0, 0x1c0);
    }
    public static void AtomicWrite(NativeMacPrivileges native, string path, byte[] bytes, uint mode = 0x180)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                Options = FileOptions.WriteThrough
            }))
            { file.Write(bytes); file.Flush(true); }
            native.SetOwnerAndMode(temporary, 0, 0, mode); File.Move(temporary, path, true); SyncDirectory(Path.GetDirectoryName(path)!);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static void SyncDirectory(string path)
    {
        int fd = Open(path, 0); if (fd < 0) throw new IOException("Directory synchronization could not start.");
        try { if (Fsync(fd) != 0) throw new IOException("Directory synchronization failed."); } finally { Close(fd); }
    }
    public static void SignalTerminate(int pid) { if (Kill(pid, 15) != 0) throw new IOException("The owned core could not be stopped."); }
    [LibraryImport("libc", EntryPoint = "kill")] private static partial int Kill(int pid, int signal);
    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8)] private static partial int Open(string path, int flags);
    [LibraryImport("libc", EntryPoint = "fsync")] private static partial int Fsync(int fd);
    [LibraryImport("libc", EntryPoint = "close")] private static partial int Close(int fd);
}
