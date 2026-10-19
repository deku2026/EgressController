using System.ComponentModel;
using System.Runtime.InteropServices;
using EgressController.Core.Protection;

namespace EgressController.Launcher.Discovery;

/// <summary>Read-only Windows identity/known-folder queries. Never create package data or
/// infer a family by stripping a version/name prefix.</summary>
internal static class WindowsPackageLocations
{
    private static readonly Guid LocalAppData = new("f1b32785-6fba-4fcf-9d55-7b8e7f157091");

    public static string CurrentUserLocalAppData()
    {
        RuntimeSafety.RequireLiveOperations();
        nint path = 0;
        try
        {
            // An MSIX build of this controller must not resolve another package inside its
            // own virtualized AppData. No KF_FLAG_CREATE: discovery never creates directories.
            Guid folder = LocalAppData;
            int result = SHGetKnownFolderPath(ref folder, 0x00010000, 0, out path); // KF_FLAG_NO_PACKAGE_REDIRECTION
            Marshal.ThrowExceptionForHR(result);
            return Marshal.PtrToStringUni(path) is { Length: > 0 } value ? value : throw new IOException("Windows 未返回 LocalAppData 路径。");
        }
        finally { if (path != 0) Marshal.FreeCoTaskMem(path); }
    }

    public static string FamilyFromFullName(string fullName)
    {
        RuntimeSafety.RequireLiveOperations();
        uint length = 0;
        int result = PackageFamilyNameFromFullName(fullName, ref length, null);
        if (result != 122 || length is 0 or > 256) throw new Win32Exception(result, "无法解析 Windows 包家族：" + fullName);
        char[] value = new char[length];
        result = PackageFamilyNameFromFullName(fullName, ref length, value);
        if (result != 0) throw new Win32Exception(result, "无法解析 Windows 包家族：" + fullName);
        int terminator = Array.IndexOf(value, '\0');
        string family = new(value, 0, terminator >= 0 ? terminator : value.Length);
        if (!PackageExecutableRoots.IsFamilyName(family)) throw new IOException("Windows 返回了无效的包家族标识。");
        return family;
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, nint token, out nint path);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int PackageFamilyNameFromFullName(string fullName, ref uint length, [Out] char[]? family);
}
