using EgressController.Core.Contracts;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace EgressController.Windows.Process;

/// <summary>
/// Canonicalize a Windows path via GetFinalPathNameByHandle on the opened file, resolving
/// junctions / symlinks / <c>..</c> / 8.3 aliases before ownership containment (plan §1.6).
/// The canonical result has the <c>\\?\</c> prefix stripped; casing/separators are normalized.
/// Any failure returns null — and a null final path is NEVER treated as managed.
/// </summary>
public sealed unsafe class ExecutablePathCanonicalizer : IExecutablePathCanonicalizer
{
    public string? Canonicalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            using var handle = PInvoke.CreateFile(path, 0,
                FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
                null, FILE_CREATION_DISPOSITION.OPEN_EXISTING, FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS, null);
            if (handle.IsInvalid) return null;
            uint needed = PInvoke.GetFinalPathNameByHandle(new HANDLE(handle.DangerousGetHandle()), null, 0, GETFINALPATHNAMEBYHANDLE_FLAGS.FILE_NAME_NORMALIZED);
            if (needed == 0)
                return null;

            var buffer = new char[needed + 1];
            uint written;
            fixed (char* p = buffer)
            {
                written = PInvoke.GetFinalPathNameByHandle(new HANDLE(handle.DangerousGetHandle()), new PWSTR(p), (uint)buffer.Length, GETFINALPATHNAMEBYHANDLE_FLAGS.FILE_NAME_NORMALIZED);
            }
            if (written == 0 || written >= buffer.Length)
                return null;

            return NormalizeFinalPath(new string(buffer, 0, (int)written));
        }
        catch
        {
            return null;
        }
    }

    internal static string NormalizeFinalPath(string canonical)
    {
        if (canonical.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            canonical = @"\\" + canonical[8..];
        else if (canonical.StartsWith(@"\\?\", StringComparison.Ordinal))
            canonical = canonical[4..];
        return canonical.Replace('/', Path.DirectorySeparatorChar);
    }
}
