namespace EgressController.Core.Contracts;

/// <summary>Kinds of interface/route change delivered to subscribers for ESIM re-resolution.</summary>
public enum InterfaceChangeKind
{
    /// <summary>Interface added / enabled / connected.</summary>
    Added,
    /// <summary>Interface removed / disabled / disconnected.</summary>
    Removed,
    /// <summary>Address or operational state changed.</summary>
    Changed,
    /// <summary>A route table change was observed.</summary>
    RouteChanged,
}

/// <summary>A single interface/route event. ifIndex may be 0 for route-only events.</summary>
public readonly record struct InterfaceChangeEvent(InterfaceChangeKind Kind, int IfIndex);

/// <summary>
/// Watches the interface/route table so the controller can re-resolve the ESIM adapter
/// identity after hotplug or reconnection, without poll/restart. Events are raised on a worker
/// thread; subscribers must not block.
/// </summary>
public interface INetworkInterfaceMonitor : IAsyncDisposable
{
    void Start();
    event Action<InterfaceChangeEvent>? Changed;
}