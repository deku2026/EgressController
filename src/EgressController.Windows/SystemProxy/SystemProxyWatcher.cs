using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using EgressController.Core.Models;

namespace EgressController.Windows.SystemProxy;

/// <summary>
/// Watches the current-user Internet Settings key. Registry notifications are one-shot, so the
/// watcher re-registers after every change and reports a complete proxy snapshot to its owner.
/// </summary>
public sealed class SystemProxyWatcher : IDisposable
{
    private const uint RegNotifyChangeLastSet = 0x00000004;
    private const int ErrorSuccess = 0;

    private readonly SystemProxyManager _manager;
    private readonly Action<SystemProxyState> _onChanged;
    private readonly EventWaitHandle _changed = new(false, EventResetMode.AutoReset);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;

    internal SystemProxyWatcher(SystemProxyManager manager, Action<SystemProxyState> onChanged)
    {
        _manager = manager;
        _onChanged = onChanged;
        _worker = Task.Run(WatchLoop);
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _changed.Set(); } catch (ObjectDisposedException) { }
        try { _worker.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        _changed.Dispose();
        _stop.Dispose();
    }

    private void WatchLoop()
    {
        WaitHandle[] waits = [_changed, _stop.Token.WaitHandle];
        while (!_stop.IsCancellationRequested)
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                SystemProxyManager.InternetSettingsKey, writable: false);
            if (key is null)
                return;

            SafeRegistryHandle handle = key.Handle;
            int result = RegNotifyChangeKeyValue(
                handle.DangerousGetHandle(),
                watchSubtree: false,
                RegNotifyChangeLastSet,
                _changed.SafeWaitHandle.DangerousGetHandle(),
                asynchronous: true);
            if (result != ErrorSuccess)
                return;

            int signaled = WaitHandle.WaitAny(waits, 1000);
            if (signaled == 0 && !_stop.IsCancellationRequested)
            {
                try { _onChanged(_manager.Snapshot()); } catch { /* observer is advisory */ }
            }
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegNotifyChangeKeyValue(
        nint hKey,
        [MarshalAs(UnmanagedType.Bool)] bool watchSubtree,
        uint notifyFilter,
        nint hEvent,
        [MarshalAs(UnmanagedType.Bool)] bool asynchronous);
}
