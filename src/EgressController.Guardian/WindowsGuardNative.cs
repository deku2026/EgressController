using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using EgressController.Core.Protection;
using EgressController.Windows.Network;
using EgressController.Windows.Process;

namespace EgressController.Guardian;

internal static partial class GuardNative
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceStatus
    {
        public uint Type, State, Accepted, Win32Exit, ServiceExit, Checkpoint, WaitHint;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceStatusProcess
    {
        public uint Type, State, Accepted, Win32Exit, ServiceExit, Checkpoint, WaitHint, Pid, Flags;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceConfig
    {
        public uint Type, Start, Error;
        public nint Binary, Group;
        public uint Tag;
        public nint Dependencies, Account, DisplayName;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct BootEnvironment { public Guid Id; public uint FirmwareType; public ulong Flags; }
    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint OpenScm(string? machine, string? database, uint access);
    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint OpenService(nint scm, string name, uint access);
    [LibraryImport("advapi32.dll", EntryPoint = "CreateServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateService(nint scm, string name, string display, uint access, uint type,
        uint start, uint error, string binary, string? group, nint tag, string? dependencies, string? account, string? password);
    [LibraryImport("advapi32.dll", SetLastError = true)]
    internal static partial int CloseServiceHandle(nint handle);
    [LibraryImport("advapi32.dll", SetLastError = true)]
    internal static partial int QueryServiceStatusEx(nint service, int level, out ServiceStatusProcess status, uint size, out uint needed);
    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", SetLastError = true)]
    internal static partial int QueryServiceConfig(nint service, nint config, uint size, out uint needed);
    [LibraryImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true)]
    internal static partial int StartService(nint service, uint count, nint arguments);
    [LibraryImport("advapi32.dll", SetLastError = true)]
    internal static partial int ControlService(nint service, uint control, out ServiceStatus status);
    [LibraryImport("advapi32.dll", SetLastError = true)]
    internal static partial int DeleteService(nint service);
    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int ChangeServiceConfig(nint service, uint type, uint start, uint error, string? binary,
        string? group, nint tag, string? dependencies, string? account, string? password, string? display);
    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
    internal static partial int ChangeServiceConfig2(nint service, uint level, nint value);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint OpenProcess(uint access, int inherit, uint pid);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int CloseHandle(nint handle);
    [LibraryImport("advapi32.dll", SetLastError = true)]
    internal static partial int OpenProcessToken(nint process, uint access, out nint token);
    [LibraryImport("advapi32.dll", SetLastError = true)]
    internal static partial int GetTokenInformation(nint token, int infoClass, nint info, uint size, out uint needed);
    [LibraryImport("ntdll.dll")]
    internal static partial int NtQuerySystemInformation(int infoClass, out BootEnvironment info, uint length, out uint returned);

    internal static Win32Exception Error() => new(Marshal.GetLastPInvokeError());
    internal static void Check(int result) { if (result == 0) throw Error(); }
}

public static class GuardWindowsIdentity
{
    public static string BootId
    {
        get
        {
            RuntimeSafety.RequireLiveOperations();
            int result = GuardNative.NtQuerySystemInformation(90, out var boot,
                (uint)Marshal.SizeOf<GuardNative.BootEnvironment>(), out _);
            if (result < 0) throw new InvalidOperationException("无法读取 Windows 启动会话：" + result.ToString("X", CultureInfo.InvariantCulture));
            return boot.Id.ToString("N");
        }
    }
    public static string ProcessSid(uint pid)
    {
        RuntimeSafety.RequireLiveOperations();
        nint process = GuardNative.OpenProcess(0x1000, 0, pid);
        if (process == 0) throw GuardNative.Error();
        try
        {
            GuardNative.Check(GuardNative.OpenProcessToken(process, 8, out nint token));
            try
            {
                GuardNative.GetTokenInformation(token, 1, 0, 0, out uint size);
                nint buffer = Marshal.AllocHGlobal(checked((int)size));
                try
                {
                    GuardNative.Check(GuardNative.GetTokenInformation(token, 1, buffer, size, out _));
                    return new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { GuardNative.CloseHandle(token); }
        }
        finally { GuardNative.CloseHandle(process); }
    }
    public static bool Elevated => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
}

public sealed class WindowsGuardFacts : IGuardFacts
{
    private readonly WindowsProcessIdentityResolver _identities = new(new ExecutablePathCanonicalizer());
    private readonly WindowsTunInspector _tun = new();
    private ProcessKey? _core;
    public bool IsAlive(ProcessKey identity, string executable)
    {
        RuntimeSafety.RequireLiveOperations();
        var current = _identities.Resolve(identity.Pid);
        return current is not null && current.StartTimeUtc == identity.StartedAtUtc
            && string.Equals(current.ExePathFinal, executable, StringComparison.OrdinalIgnoreCase);
    }
    public string? InspectTun(ProcessKey core, string executable)
    {
        if (!IsAlive(core, executable)) return "受管理 sing-box 实例已退出";
        if (_core != core) { _tun.Reset(); _core = core; }
        return _tun.GetError();
    }
}

/// <summary>LocalSystem must not terminate another user's matching executable.</summary>
public sealed class UserScopedProcessControl(IProcessControl inner, string ownerSid, Func<uint, string>? resolveSid = null) : IProcessControl
{
    private readonly Func<uint, string> _resolveSid = resolveSid ?? GuardWindowsIdentity.ProcessSid;
    public IReadOnlyList<ProcessInspectionFailure> InspectionFailures { get; private set; } = [];
    public IReadOnlyList<GuardProcess> Capture()
    {
        var captured = inner.Capture();
        var failures = inner.InspectionFailures.ToList();
        var result = new List<GuardProcess>();
        foreach (var process in captured)
        {
            try { if (_resolveSid(process.Key.Pid) == ownerSid) result.Add(process); }
            catch (Win32Exception error) when (error.NativeErrorCode == 87) { /* Already exited. */ }
            catch (Exception error)
            {
                failures.Add(new(process.Key.Pid, Path.GetFileName(process.ExecutablePath),
                    "无法确认进程所属账号：" + error.Message, process.ExecutablePath));
            }
        }
        InspectionFailures = failures;
        return result;
    }
    public TerminationResult Terminate(GuardProcess process)
    {
        try
        {
            if (_resolveSid(process.Key.Pid) != ownerSid)
                return new(false, "进程不属于守护账号，拒绝终止。");
            return inner.Terminate(process);
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 87) { return new(true, AlreadyExited: true); }
        catch (Exception error) { return new(false, error.Message); }
    }
}
