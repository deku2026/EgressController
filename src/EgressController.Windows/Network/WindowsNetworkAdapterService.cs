using System.Net;
using System.Runtime.InteropServices;
using EgressController.Core.Contracts;
using EgressController.Core.Models;
using Windows.Win32;
using Windows.Win32.NetworkManagement.IpHelper;
using Windows.Win32.NetworkManagement.Ndis;
using Windows.Win32.Networking.WinSock;

namespace EgressController.Windows.Network;

/// <summary>
/// Enumerates adapters via GetAdaptersAddresses (CsWin32 -> IPHLPAPI.dll). Reads IPv4/IPv6
/// unicast + gateway + DNS addresses, stable GUID, LUID, and run-time ifIndex. AOT-safe
/// (unmanaged buffer via NativeMemory, no reflection).
/// </summary>
public sealed unsafe class WindowsNetworkAdapterService : INetworkAdapterService
{
    private const uint AfUnspec = (uint)ADDRESS_FAMILY.AF_UNSPEC;
    private const GET_ADAPTERS_ADDRESSES_FLAGS Flags =
        GET_ADAPTERS_ADDRESSES_FLAGS.GAA_FLAG_INCLUDE_PREFIX |
        GET_ADAPTERS_ADDRESSES_FLAGS.GAA_FLAG_INCLUDE_GATEWAYS |
        // Include disabled/disconnected adapters too, so the UI can still list e.g. an
        // unconnected ESIM adapter for selection ("enum 全部相关物理/虚拟接口").
        GET_ADAPTERS_ADDRESSES_FLAGS.GAA_FLAG_INCLUDE_ALL_INTERFACES;

    public IReadOnlyList<NetworkAdapterInfo> EnumerateAll()
    {
        uint size = 0;
        // First call with empty buffer: returns ERROR_BUFFER_OVERFLOW and reports required size.
        _ = PInvoke.GetAdaptersAddresses(AfUnspec, Flags, Reserved: null, AdapterAddresses: null, SizePointer: &size);
        if (size == 0)
            return Array.Empty<NetworkAdapterInfo>();

        // Slack so an adapter appearing between the two calls cannot trip a buffer overflow.
        size += 16 * 1024;

        byte* buffer = (byte*)NativeMemory.Alloc(size);
        try
        {
            uint rc = PInvoke.GetAdaptersAddresses(AfUnspec, Flags, Reserved: null, AdapterAddresses: (IP_ADAPTER_ADDRESSES_LH*)buffer, SizePointer: &size);
            if (rc != 0)
                return Array.Empty<NetworkAdapterInfo>();

            var result = new List<NetworkAdapterInfo>();
            for (var cur = (IP_ADAPTER_ADDRESSES_LH*)buffer; cur is not null; cur = cur->Next)
                result.Add(ReadAdapter(cur));
            return result;
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    public NetworkAdapterInfo? GetByGuid(Guid guid)
        => EnumerateAll().FirstOrDefault(a => a.Identity.Guid == guid);

    public NetworkAdapterInfo? GetByIfIndex(int ifIndex)
        => EnumerateAll().FirstOrDefault(a => a.IfIndex == ifIndex);

    /// <summary>Interface index that owns the 0.0.0.0 default route (= PRIMARY egress), or 0 if none.</summary>
    public int GetDefaultRouteInterfaceIndex()
    {
        uint best = 0;
        _ = PInvoke.GetBestInterface(0, &best);
        return (int)best;
    }

    private static NetworkAdapterInfo ReadAdapter(IP_ADAPTER_ADDRESSES_LH* cur)
    {
        int ifIndex = (int)cur->Anonymous1.IfIndex;
        int ipv6IfIndex = (int)cur->Ipv6IfIndex;

        Guid guid = Guid.TryParse(cur->AdapterName.ToString(), out Guid g) ? g : Guid.Empty;
        string friendlyName = cur->FriendlyName.ToString() ?? string.Empty;
        string description = cur->Description.ToString() ?? string.Empty;

        var addresses = new List<IPAddress>();
        for (var a = cur->FirstUnicastAddress; a is not null; a = a->Next)
            if (TryAddress(a->Address, out IPAddress ip))
                addresses.Add(ip);

        var gateways = new List<IPAddress>();
        for (var gw = cur->FirstGatewayAddress; gw is not null; gw = gw->Next)
            if (TryAddress(gw->Address, out IPAddress ip))
                gateways.Add(ip);

        var dnsServers = new List<IPAddress>();
        for (var d = cur->FirstDnsServerAddress; d is not null; d = d->Next)
            if (TryAddress(d->Address, out IPAddress ip))
                dnsServers.Add(ip);

        return new NetworkAdapterInfo
        {
            Identity = new NetworkAdapterIdentity(guid, friendlyName),
            Description = description,
            Luid = cur->Luid.Value,
            IfIndex = ifIndex,
            Ipv6IfIndex = ipv6IfIndex,
            IsUp = cur->OperStatus == IF_OPER_STATUS.IfOperStatusUp,
            Addresses = addresses,
            Gateways = gateways,
            DnsServers = dnsServers,
        };
    }

    /// <summary>Converts a SOCKET_ADDRESS to a managed IPAddress using the raw sockaddr bytes.</summary>
    private static bool TryAddress(SOCKET_ADDRESS sa, out IPAddress ip)
    {
        ip = null!;
        if (sa.lpSockaddr is null)
            return false;

        ushort family = *(ushort*)sa.lpSockaddr;
        byte* p = (byte*)sa.lpSockaddr;

        switch (family)
        {
            // sockaddr_in: family(2) port(2) address(4)
            case (ushort)ADDRESS_FAMILY.AF_INET:
            {
                Span<byte> b4 = stackalloc byte[4];
                for (int i = 0; i < 4; i++)
                    b4[i] = p[4 + i];
                ip = new IPAddress(b4);
                return true;
            }
            // sockaddr_in6: family(2) port(2) flowinfo(4) address(16)
            case (ushort)ADDRESS_FAMILY.AF_INET6:
            {
                Span<byte> b16 = stackalloc byte[16];
                for (int i = 0; i < 16; i++)
                    b16[i] = p[8 + i];
                ip = new IPAddress(b16);
                return true;
            }
            default:
                return false;
        }
    }
}