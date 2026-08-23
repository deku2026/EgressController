using System.Net;
using System.Net.Sockets;

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

    /// <summary>Windows IF_TYPE value. It is runtime metadata, never persisted in Profile.</summary>
    public uint InterfaceType { get; init; }

    public AdapterAddressState AddressState
    {
        get
        {
            if (!IsUp)
                return AdapterAddressState.Offline;

            bool hasV4 = GetUsableAddresses(AddressFamily.InterNetwork).Count > 0;
            bool hasV6 = GetUsableAddresses(AddressFamily.InterNetworkV6).Count > 0;
            return (hasV4, hasV6) switch
            {
                (true, true) => AdapterAddressState.DualStack,
                (true, false) => AdapterAddressState.Ipv4Only,
                (false, true) => AdapterAddressState.Ipv6Only,
                _ => AdapterAddressState.NoAddress,
            };
        }
    }

    public IPAddress? Ipv4BindAddress
        => GetUsableAddresses(AddressFamily.InterNetwork).FirstOrDefault();

    public IPAddress? Ipv6BindAddress
        => GetUsableAddresses(AddressFamily.InterNetworkV6).FirstOrDefault();

    public IReadOnlyList<IPAddress> GetUsableAddresses(AddressFamily family)
        => Addresses
            .Where(address => address.AddressFamily == family && IsUsableUnicast(address))
            .Distinct()
            .OrderBy(address => address.ToString(), StringComparer.Ordinal)
            .ToArray();

    private static bool IsUsableUnicast(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.IsIPv4MappedToIPv6 || address.GetAddressBytes().All(b => b == 0))
            return false;

        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            // 169.254/16 is link-local and cannot be used as a stable Internet source.
            return !(bytes[0] == 169 && bytes[1] == 254);
        }

        // fe80::/10 is IPv6 link-local. ULA is retained because it can be the valid source for
        // a carrier/private eSIM route, while multicast/unspecified addresses are excluded above.
        return !(bytes[0] == 0xff || (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80));
    }
}

public enum AdapterAddressState
{
    Offline = 0,
    NoAddress = 1,
    Ipv4Only = 2,
    Ipv6Only = 3,
    DualStack = 4,
}
