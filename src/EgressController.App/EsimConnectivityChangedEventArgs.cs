namespace EgressController.App;

/// <summary>One stable eSIM connectivity transition observed by the runtime monitor.</summary>
public sealed class EsimConnectivityChangedEventArgs : EventArgs
{
    public required bool IsOnline { get; init; }
    public required string AdapterName { get; init; }
    public required DateTimeOffset DetectedAtUtc { get; init; }

    /// <summary>Connections synchronously asked to close before an offline event is raised.</summary>
    public int ClosedConnections { get; init; }
}
