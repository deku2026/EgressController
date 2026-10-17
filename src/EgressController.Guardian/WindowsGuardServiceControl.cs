using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using EgressController.Core.Protection;

namespace EgressController.Guardian;

public sealed class WindowsGuardServiceControl(GuardPaths paths)
{
    private const uint QueryRights = 1 | 4;
    public GuardServiceInfo Query()
    {
        RuntimeSafety.RequireLiveOperations();
        using var scm = Handle.Scm(1);
        nint raw = GuardNative.OpenService(scm.Value, paths.ServiceName, QueryRights);
        if (raw == 0)
        {
            if (Marshal.GetLastPInvokeError() == 1060) return new(paths.ServiceName, GuardServiceState.Missing, false, 0, paths.ServiceExecutable, "LocalSystem");
            throw GuardNative.Error();
        }
        using var service = new Handle(raw);
        var status = ReadStatus(service.Value);
        GuardNative.QueryServiceConfig(service.Value, 0, 0, out uint needed);
        nint buffer = Marshal.AllocHGlobal(checked((int)needed));
        try
        {
            GuardNative.Check(GuardNative.QueryServiceConfig(service.Value, buffer, needed, out _));
            var config = Marshal.PtrToStructure<GuardNative.ServiceConfig>(buffer);
            return new(paths.ServiceName, status.State switch
            {
                1 => GuardServiceState.Stopped, 2 => GuardServiceState.Starting,
                3 => GuardServiceState.Stopping, 4 => GuardServiceState.Running, _ => GuardServiceState.Unknown,
            }, config.Start == 2, status.Pid, Marshal.PtrToStringUni(config.Binary) ?? "",
                Marshal.PtrToStringUni(config.Account) ?? "", status.Win32Exit == 0 ? null : new Win32Exception((int)status.Win32Exit).Message);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    public void Install(bool autoStart)
    {
        RuntimeSafety.RequireLiveOperations();
        using var scm = Handle.Scm(1 | 2);
        string binary = paths.ServiceCommandLine;
        using var service = new Handle(GuardNative.CreateService(scm.Value, paths.ServiceName,
            "EgressController 独立守护 (" + paths.OwnerSid + ")", 0xF01FF, 0x10, autoStart ? 2u : 3u,
            1, binary, null, 0, null, null, null));
        ConfigureRecovery(service.Value);
    }
    public void Start()
    {
        RuntimeSafety.RequireLiveOperations();
        using var service = Open(0x10 | 4);
        var status = ReadStatus(service.Value);
        if (status.State == 4) return;
        if (status.State != 2)
        {
            if (GuardNative.StartService(service.Value, 0, 0) == 0 && Marshal.GetLastPInvokeError() != 1056)
                throw GuardNative.Error();
        }
        Wait(service.Value, 4);
    }
    public void Stop(bool force)
    {
        RuntimeSafety.RequireLiveOperations();
        using var service = Open(0x20 | 4);
        var status = ReadStatus(service.Value);
        if (status.State == 1) return;
        if (force)
        {
            // The SCM identity, private service type, executable and process creation time are
            // checked; never terminate by a process name or a cached PID.
            if (status.Type != 0x10 || status.Pid == 0) throw new InvalidOperationException("无法确认独占守护服务进程。");
            using var process = System.Diagnostics.Process.GetProcessById((int)status.Pid);
            string? executable = process.MainModule?.FileName;
            if (!string.Equals(executable, paths.ServiceExecutable, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("服务进程路径不匹配，拒绝强制终止。");
            var identity = new ProcessKey(status.Pid, process.StartTime.ToUniversalTime());
            var result = new EgressController.Windows.Process.WindowsProcessControl()
                .Terminate(new(identity, 0, executable!));
            if (!result.Succeeded) throw new InvalidOperationException(result.Error);
        }
        else if (status.State != 3)
            GuardNative.Check(GuardNative.ControlService(service.Value, 1, out _));
        Wait(service.Value, 1);
    }
    public void SetAutoStart(bool enabled)
    {
        RuntimeSafety.RequireLiveOperations();
        using var service = Open(2);
        GuardNative.Check(GuardNative.ChangeServiceConfig(service.Value, uint.MaxValue, enabled ? 2u : 3u,
            uint.MaxValue, null, null, 0, null, null, null, null));
    }
    public void Delete()
    {
        RuntimeSafety.RequireLiveOperations();
        if (Query().State == GuardServiceState.Missing) return;
        Stop(false);
        using var service = Open(0x10000);
        GuardNative.Check(GuardNative.DeleteService(service.Value));
    }
    private Handle Open(uint access)
    {
        using var scm = Handle.Scm(1);
        return new(GuardNative.OpenService(scm.Value, paths.ServiceName, access));
    }
    private static GuardNative.ServiceStatusProcess ReadStatus(nint service)
    {
        GuardNative.Check(GuardNative.QueryServiceStatusEx(service, 0, out var status,
            (uint)Marshal.SizeOf<GuardNative.ServiceStatusProcess>(), out _));
        return status;
    }
    private static void Wait(nint service, uint desired)
    {
        var elapsed = Stopwatch.StartNew();
        do
        {
            var status = ReadStatus(service);
            if (status.State == desired) return;
            if (desired == 4 && status.State == 1) throw new Win32Exception((int)status.Win32Exit, "守护服务启动失败。");
            Thread.Sleep(100);
        } while (elapsed.Elapsed < TimeSpan.FromSeconds(10));
        throw new TimeoutException("服务状态变更超时，可查看错误或使用强制停止。");
    }
    [StructLayout(LayoutKind.Sequential)] private struct FailureAction { public uint Type, Delay; }
    [StructLayout(LayoutKind.Sequential)] private struct FailureActions
    {
        public uint ResetPeriod; public nint RebootMessage, Command; public uint Count; public nint Actions;
    }
    private static unsafe void ConfigureRecovery(nint service)
    {
        FailureAction* actions = stackalloc FailureAction[3];
        actions[0] = new() { Type = 1, Delay = 3000 };
        actions[1] = new() { Type = 1, Delay = 10000 };
        actions[2] = new() { Type = 1, Delay = 30000 };
        var recovery = new FailureActions { ResetPeriod = 86400, Count = 3, Actions = (nint)actions };
        GuardNative.Check(GuardNative.ChangeServiceConfig2(service, 2, (nint)(&recovery)));
        int recoverNonzeroExit = 1;
        GuardNative.Check(GuardNative.ChangeServiceConfig2(service, 4, (nint)(&recoverNonzeroExit)));
    }
    private sealed class Handle : IDisposable
    {
        public nint Value { get; }
        public Handle(nint value) { if (value == 0) throw GuardNative.Error(); Value = value; }
        public static Handle Scm(uint access) => new(GuardNative.OpenScm(null, null, access));
        public void Dispose() => GuardNative.CloseServiceHandle(Value);
    }
}
