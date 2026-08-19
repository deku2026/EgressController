using System.Net;

namespace EgressController.Core.Models;

/// <summary>
/// Stable, user-visible identity of an adapter (plan §Step 01 "稳定 identity").
/// Persist <see cref="Guid"/> + <see cref="NameSnapshot"/>; resolve to a runtime
/// <see cref="NetworkAdapterInfo"/> when connecting. ifIndex/LUID are NOT persisted.
/// </summary>
public sealed record NetworkAdapterIdentity(Guid Guid, string NameSnapshot);

/// <summary>
/// Runtime snapshot of a network adapter (plan §6 data model). identity.Guid pins the
/// adapter across FriendlyName renames / interface-index churn.
/// </summary>
public sealed class NetworkAdapterInfo
{
    public required NetworkAdapterIdentity Identity { get; init; }

    public required string Description { get; init; }

    /// <summary>Run-time interface LUID (used by Connection Policy in Step 03).</summary>
    public required ulong Luid { get; init; }

    /// <summary>Run-time interface index (IPv4; used by IP_UNICAST_IF in Step 02).</summary>
    public required int IfIndex { get; init; }

    /// <summary>Run-time IPv6 interface index.</summary>
    public required int Ipv6IfIndex { get; init; }

    public required bool IsUp { get; init; }

    public required IReadOnlyList<IPAddress> Addresses { get; init; }

    public required IReadOnlyList<IPAddress> Gateways { get; init; }

    public required IReadOnlyList<IPAddress> DnsServers { get; init; }
}