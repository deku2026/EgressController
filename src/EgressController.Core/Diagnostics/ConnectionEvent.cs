using EgressController.Core.Routing;

namespace EgressController.Core.Diagnostics;

public enum ConnectionStatus
{
    Accepted,
    Decided,
    Established,
    Failed,
    Closed,
}

/// <summary>One observable connection/request row for the Connection Log (plan §Step 11).</summary>
public sealed record ConnectionEvent(
    DateTimeOffset TimestampUtc,
    uint? SourcePid,
    string ProcessName,
    string? FinalExePath,
    string? SessionId,
    string Host,
    int Port,
    Egress Egress,
    RouteReason Reason,
    string? RuleSet,
    string? RuleText,
    string Interface,
    ConnectionStatus Status,
    long Bytes,
    TimeSpan Latency);

/// <summary>
/// Where ConnectionLog events are written from the data plane. Kept in Core so the Proxy (which
/// must not reference the Diagnostics assembly, plan §2.4) can emit observability without being
/// coupled to the ring-buffer implementation.
/// </summary>
public interface IConnectionLog
{
    void Write(ConnectionEvent e);
}