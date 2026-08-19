using System.Threading.Channels;
using EgressController.Core.Contracts;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.NetworkManagement.IpHelper;
using Windows.Win32.Networking.WinSock;

namespace EgressController.Windows.Network;

/// <summary>
/// Registers NotifyIpInterfaceChange + NotifyRouteChange2 so the controller can re-resolve the
/// ESIM adapter identity after hotplug/reconnect without restart. Native callbacks only marshal
/// into a bounded channel (drop-oldest); a safe pump task raises the managed event off that
/// thread, so no business I/O ever runs inside a native callback (plan §Step 02).
/// </summary>
public sealed unsafe class WindowsNetworkInterfaceMonitor : INetworkInterfaceMonitor
{
    private readonly Channel<InterfaceChangeEvent> _events =
        Channel.CreateBounded<InterfaceChangeEvent>(new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropWrite });

    private readonly PIPINTERFACE_CHANGE_CALLBACK _interfaceCallback;
    private readonly PIPFORWARD_CHANGE_CALLBACK _routeCallback;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private HANDLE _interfaceHandle;
    private HANDLE _routeHandle;
    private int _started;

    public WindowsNetworkInterfaceMonitor()
    {
        _interfaceCallback = OnInterfaceChange;
        _routeCallback = OnRouteChange;
    }

    public event Action<InterfaceChangeEvent>? Changed;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            return;

        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _cts = cts;
        }

        PInvoke.NotifyIpInterfaceChange(
            ADDRESS_FAMILY.AF_UNSPEC, _interfaceCallback, CallerContext: null, InitialNotification: false, ref _interfaceHandle);
        PInvoke.NotifyRouteChange2(
            ADDRESS_FAMILY.AF_UNSPEC, _routeCallback, CallerContext: null, InitialNotification: false, ref _routeHandle);

        _worker = Task.Run(() => InterfaceChangePump.Run(_events.Reader, Raise, cts.Token));
    }

    private void Raise(InterfaceChangeEvent e) => Changed?.Invoke(e);

    private unsafe void OnInterfaceChange(void* callerContext, MIB_IPINTERFACE_ROW* row, MIB_NOTIFICATION_TYPE notificationType)
    {
        int ifIndex = row is not null ? (int)row->InterfaceIndex : 0;
        var kind = notificationType switch
        {
            MIB_NOTIFICATION_TYPE.MibAddInstance => InterfaceChangeKind.Added,
            MIB_NOTIFICATION_TYPE.MibDeleteInstance => InterfaceChangeKind.Removed,
            _ => InterfaceChangeKind.Changed,
        };
        _events.Writer.TryWrite(new InterfaceChangeEvent(kind, ifIndex));
    }

    private unsafe void OnRouteChange(void* callerContext, MIB_IPFORWARD_ROW2* row, MIB_NOTIFICATION_TYPE notificationType)
    {
        ulong ifIndex = row is not null ? row->InterfaceIndex : 0;
        _events.Writer.TryWrite(new InterfaceChangeEvent(InterfaceChangeKind.RouteChanged, (int)ifIndex));
    }

    public ValueTask DisposeAsync()
    {
        CancellationTokenSource? cts;
        Task? worker;
        lock (_gate)
        {
            cts = _cts;
            worker = _worker;
            _cts = null;
            _worker = null;
        }

        cts?.Cancel();

        if (_started == 1)
        {
            PInvoke.CancelMibChangeNotify2(_interfaceHandle);
            PInvoke.CancelMibChangeNotify2(_routeHandle);
            Interlocked.Exchange(ref _started, 0);
        }

        cts?.Dispose();
        _ = worker; // drain worker tears down on cancellation; not awaited to stay non-blocking.
        return ValueTask.CompletedTask;
    }
}

/// <summary>Awaits the event channel on a safe context (no unsafe pointers).</summary>
internal static class InterfaceChangePump
{
    public static async Task Run(
        ChannelReader<InterfaceChangeEvent> reader,
        Action<InterfaceChangeEvent> raise,
        CancellationToken cancellationToken)
    {
        await foreach (InterfaceChangeEvent e in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            raise(e);
    }
}