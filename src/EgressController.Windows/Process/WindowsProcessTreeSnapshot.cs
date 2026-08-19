using System.Runtime.InteropServices;

namespace EgressController.Windows.Process;

/// <summary>Small Toolhelp snapshot used by the launcher reconciler, never by the network thread.</summary>
public sealed partial class WindowsProcessTreeSnapshot
{
    public IReadOnlyDictionary<uint, uint> ParentByPid { get; }

    private WindowsProcessTreeSnapshot(Dictionary<uint, uint> parentByPid)
        => ParentByPid = parentByPid;

    public static WindowsProcessTreeSnapshot Capture()
    {
        const uint SnapshotProcess = 0x00000002;
        IntPtr snapshot = ToolhelpNative.CreateToolhelp32Snapshot(SnapshotProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            return new WindowsProcessTreeSnapshot(new Dictionary<uint, uint>());

        try
        {
            var result = new Dictionary<uint, uint>();
            var entry = new ToolhelpNative.ProcessEntry32 { Size = (uint)Marshal.SizeOf<ToolhelpNative.ProcessEntry32>() };
            if (!ToolhelpNative.Process32First(snapshot, ref entry))
                return new WindowsProcessTreeSnapshot(result);

            do
            {
                result[entry.ProcessId] = entry.ParentProcessId;
            }
            while (ToolhelpNative.Process32Next(snapshot, ref entry));

            return new WindowsProcessTreeSnapshot(result);
        }
        finally
        {
            ToolhelpNative.CloseHandle(snapshot);
        }
    }

    public IReadOnlySet<uint> DescendantsOf(uint rootPid)
    {
        var result = new HashSet<uint> { rootPid };
        bool changed;
        do
        {
            changed = false;
            foreach ((uint pid, uint parent) in ParentByPid)
                if (result.Contains(parent) && result.Add(pid))
                    changed = true;
        }
        while (changed);
        return result;
    }

    private static partial class ToolhelpNative
    {
        [StructLayout(LayoutKind.Sequential)]
        internal unsafe struct ProcessEntry32
        {
            public uint Size;
            public uint Usage;
            public uint ProcessId;
            public nuint DefaultHeapId;
            public uint ModuleId;
            public uint Threads;
            public uint ParentProcessId;
            public int BasePriority;
            public uint Flags;
            public fixed char ExeFile[260];
        }

        [LibraryImport("kernel32.dll", EntryPoint = "CreateToolhelp32Snapshot", SetLastError = true)]
        internal static partial IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [LibraryImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

        [LibraryImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

        [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CloseHandle(IntPtr handle);
    }
}
