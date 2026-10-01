using System.ComponentModel;
using System.Runtime.InteropServices;
using EgressController.Core.Protection;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Diagnostics.ToolHelp;
using Windows.Win32.System.Threading;

namespace EgressController.Windows.Process;

public sealed unsafe class WindowsProcessControl : IProcessControl
{
    private readonly WindowsProcessIdentityResolver _identities = new(new ExecutablePathCanonicalizer());

    public IReadOnlyList<ProcessInspectionFailure> InspectionFailures { get; private set; } = [];

    public IReadOnlyList<GuardProcess> Capture()
    {
        EgressController.Core.Protection.RuntimeSafety.RequireLiveOperations();
        HANDLE snapshot = PInvoke.CreateToolhelp32Snapshot(CREATE_TOOLHELP_SNAPSHOT_FLAGS.TH32CS_SNAPPROCESS, 0);
        if (snapshot == new HANDLE((void*)(-1))) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)sizeof(PROCESSENTRY32W) };
            var result = new List<GuardProcess>();
            var failures = new List<ProcessInspectionFailure>();
            if (!PInvoke.Process32FirstW(snapshot, &entry)) throw new Win32Exception(Marshal.GetLastWin32Error());
            do
            {
                var identity = _identities.Resolve(entry.th32ProcessID);
                if (identity?.ExePathFinal is not null)
                    result.Add(new(new(identity.Pid, identity.StartTimeUtc), entry.th32ParentProcessID, identity.ExePathFinal));
                else if (entry.th32ProcessID > 4)
                    failures.Add(new(entry.th32ProcessID, entry.szExeFile.ToString(), "进程已退出或权限不足，无法确认完整路径及创建时间。"));
            } while (PInvoke.Process32NextW(snapshot, &entry));
            int error = Marshal.GetLastWin32Error();
            if (error != 18) throw new Win32Exception(error);
            InspectionFailures = failures;
            return result;
        }
        finally { PInvoke.CloseHandle(snapshot); }
    }

    public TerminationResult Terminate(GuardProcess process)
    {
        EgressController.Core.Protection.RuntimeSafety.RequireLiveOperations();
        HANDLE handle = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION
            | PROCESS_ACCESS_RIGHTS.PROCESS_TERMINATE | PROCESS_ACCESS_RIGHTS.PROCESS_SYNCHRONIZE, false, process.Key.Pid);
        if (handle.IsNull)
        {
            int error = Marshal.GetLastWin32Error();
            return error == 87 ? new(true, AlreadyExited: true) : new(false, new Win32Exception(error).Message);
        }
        try
        {
            System.Runtime.InteropServices.ComTypes.FILETIME created, exited, kernel, user;
            if (!PInvoke.GetProcessTimes(handle, &created, &exited, &kernel, &user))
                return new(false, new Win32Exception(Marshal.GetLastWin32Error()).Message);
            long fileTime = ((long)created.dwHighDateTime << 32) | (uint)created.dwLowDateTime;
            if (DateTime.FromFileTimeUtc(fileTime) != process.Key.StartedAtUtc)
                return new(true, AlreadyExited: true);
            if (!PInvoke.TerminateProcess(handle, 1))
                return new(false, new Win32Exception(Marshal.GetLastWin32Error()).Message);
            return (uint)PInvoke.WaitForSingleObject(handle, 250) == 0
                ? new(true) : new(false, "终止请求已发送，进程尚未退出，将继续检查。");
        }
        finally { PInvoke.CloseHandle(handle); }
    }
}
