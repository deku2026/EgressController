using System.Net;
using EgressController.Core.Models;
using EgressController.Core.Protection;

namespace EgressController.Core.Tests;

public sealed class TunConnectedRouteTests
{
    [Theory]
    [InlineData("240e:33d:4c0d:8900::", "240e:33d:4c0d:8900::1234", 64, "::", true)]
    [InlineData("198.51.100.0", "198.51.100.10", 24, "0.0.0.0", false)]
    public void Physical_connected_subnet_does_not_prevent_takeover(string network, string address,
        int prefix, string nextHop, bool ipv6)
        => Assert.Null(Check(network, address, prefix, prefix, nextHop, ipv6));

    [Theory]
    [InlineData("240e:33d:4c0d:8900::", "240e:33d:4c0d:8900::1234", 64, 64, "fe80::1", true)]
    [InlineData("240e:33d:4c0d:8900::", "240e:33d:4c0d:8800::1234", 64, 64, "::", true)]
    [InlineData("::", "240e:33d:4c0d:8900::1234", 1, 64, "::", true)]
    [InlineData("198.51.100.0", "198.51.100.10", 24, 24, "192.168.1.1", false)]
    [InlineData("0.0.0.0", "198.51.100.10", 1, 24, "0.0.0.0", false)]
    [InlineData("128.0.0.0", "198.51.100.10", 1, 1, "0.0.0.0", false)]
    [InlineData("198.51.100.0", "198.51.100.10", 24, 24, null, false)]
    public void Unproven_or_gateway_public_routes_still_block_takeover(string network, string address,
        int prefix, int connectedPrefix, string? nextHop, bool ipv6)
        => Assert.Contains("路由冲突", Check(network, address, prefix, connectedPrefix, nextHop, ipv6));

    private static string? Check(string network, string address, int prefix, int connectedPrefix,
        string? nextHop, bool ipv6)
    {
        var tun = Adapter(40, TunTakeover.InterfaceName,
            [IPAddress.Parse("172.19.0.1"), IPAddress.Parse("fdfe:dcba:9876::1")]);
        var physical = Adapter(7, "PRIMARY-WIFI", [IPAddress.Parse(address)],
            [new(IPAddress.Parse(address), connectedPrefix)]);
        return TunTakeover.GetError([tun, physical],
            [new(40, false), new(40, true), new(7, ipv6, IPAddress.Parse(network), prefix, 256,
                nextHop is null ? null : IPAddress.Parse(nextHop))]);
    }

    private static NetworkAdapterInfo Adapter(int index, string name, IPAddress[] addresses,
        NetworkAdapterPrefix[]? prefixes = null) => new()
    {
        Identity = new(Guid.NewGuid(), name), Description = "Mock adapter", Luid = (ulong)index,
        IfIndex = index, Ipv6IfIndex = index, IsUp = true, InterfaceType = index == 40 ? 53u : 71u,
        Addresses = addresses, ConnectedPrefixes = prefixes ?? [], Gateways = [], DnsServers = [],
    };
}
