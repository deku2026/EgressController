using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EgressController.Core.Protection;
using EgressController.Windows.Process;

namespace EgressController.Guardian;

/// <summary>Static Win32 callbacks keep the Windows service host NativeAOT-compatible.</summary>
public static unsafe partial class GuardServiceHost
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceEntry { public char* Name; public delegate* unmanaged[Stdcall]<uint, char**, void> Main; }
    [LibraryImport("advapi32.dll", EntryPoint = "StartServiceCtrlDispatcherW", SetLastError = true)]
    private static partial int Dispatch(ServiceEntry* table);
    [LibraryImport("advapi32.dll", EntryPoint = "RegisterServiceCtrlHandlerExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint Register(string name, delegate* unmanaged[Stdcall]<uint, uint, nint, nint, uint> handler, nint context);
    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int SetServiceStatus(nint service, ref GuardNative.ServiceStatus status);
    private static GuardPaths? _paths;
    private static CancellationTokenSource? _stop;
    private static nint _handle;
    private static readonly object StatusGate = new();
    private static uint _checkpoint;
    public static int Run(GuardPaths paths)
    {
        RuntimeSafety.RequireLiveOperations();
        _paths = paths;
        fixed (char* name = paths.ServiceName)
        {
            ServiceEntry* entries = stackalloc ServiceEntry[2];
            entries[0] = new() { Name = name, Main = &ServiceMain };
            entries[1] = default;
            GuardNative.Check(Dispatch(entries));
        }
        return 0;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void ServiceMain(uint count, char** arguments)
    {
        _stop = new();
        _handle = Register(_paths!.ServiceName, &Handler, 0);
        if (_handle == 0) return;
        Report(2);
        uint error = 0;
        try { GuardServiceWorker.RunAsync(_paths, () => Report(4), _stop.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception exception)
        {
            error = 1;
            try { File.WriteAllText(Path.Combine(_paths.DataDirectory, "service-error.log"), exception.ToString()); } catch { }
        }
        finally { Report(1, error); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Handler(uint control, uint eventType, nint eventData, nint context)
    {
        if (control is 1 or 5)
        {
            Report(3);
            try { _stop?.Cancel(); } catch (AggregateException) { /* No managed exception crosses the SCM callback. */ }
        }
        return 0;
    }
    private static void Report(uint state, uint error = 0)
    {
        lock (StatusGate)
        {
            if (state == 4 && _stop?.IsCancellationRequested == true) return;
            var status = new GuardNative.ServiceStatus
            {
                Type = 0x10, State = state, Accepted = state == 4 ? 1u | 4u : 0,
                Win32Exit = error == 0 ? 0u : 1066u, ServiceExit = error,
                Checkpoint = state is 2 or 3 ? ++_checkpoint : 0, WaitHint = state is 2 or 3 ? 10000u : 0,
            };
            SetServiceStatus(_handle, ref status);
        }
    }
}

public static class GuardServiceWorker
{
    public static async Task RunAsync(GuardPaths paths, Action running, CancellationToken token)
    {
        RuntimeSafety.RequireLiveOperations();
        var registrations = new GuardRegistrationStore(paths.RegistrationFile);
        var registration = registrations.Load() ?? throw new InvalidDataException("守护安装记录缺失。");
        if (registration.OwnerSid != paths.OwnerSid || registration.InstallDirectory != paths.InstallDirectory)
            throw new InvalidDataException("守护安装记录与服务身份不匹配。");
        string boot = GuardWindowsIdentity.BootId;
        if (registration.Uninstalled || registration.StoppedOnBoot == boot) return;
        var store = new GuardStore(paths.DataDirectory);
        var mailbox = new GuardPolicyMailbox(paths.DataDirectory);
        var runtime = new GuardRuntime(store,
            new UserScopedProcessControl(new WindowsProcessControl(), paths.OwnerSid), new WindowsGuardFacts(),
            [paths.ServiceExecutable, paths.TrayExecutable]);
        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var pulse = new SemaphoreSlim(0, 1);
        void Wake() { try { pulse.Release(); } catch (SemaphoreFullException) { } }
        var server = new GuardPipeServer(paths, registration, runtime, Wake);
        Task listener = server.RunAsync(stopped.Token);
        running();
        try
        {
            while (!stopped.IsCancellationRequested)
            {
                if (listener.IsCompleted) { await listener.ConfigureAwait(false); throw new IOException("守护通信服务意外停止。"); }
                try
                {
                    var current = registrations.Load() ?? throw new InvalidDataException("守护安装记录缺失。");
                    if (current.Uninstalled || current.StoppedOnBoot == boot) break;
                    if (mailbox.Load() is { } desired && desired.Revision != runtime.Status.PolicyRevision)
                    {
                        if (!string.Equals(desired.MainExecutable, registration.MainExecutable, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("待同步名单的主程序身份不匹配。");
                        runtime.SetPolicy(desired);
                    }
                    runtime.SetInfrastructureError(null);
                }
                catch (Exception error) { runtime.SetInfrastructureError("守护状态读取/同步失败，继续保护已保存名单：" + error.Message); }
                runtime.Tick();
                await pulse.WaitAsync(TimeSpan.FromSeconds(1), stopped.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            await stopped.CancelAsync().ConfigureAwait(false);
            await listener.ConfigureAwait(false);
        }
    }
}
