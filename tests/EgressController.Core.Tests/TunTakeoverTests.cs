using System.Net;
using EgressController.Core.Models;
using EgressController.Core.Protection;

namespace EgressController.Core.Tests;

public sealed class TunTakeoverTests
{
    [Fact]
    public void Process_running_without_tun_or_either_route_is_not_ready()
    {
        var tun = Adapter();
        Assert.NotNull(TunTakeover.GetError([], []));
        Assert.Contains("IPv4", TunTakeover.GetError([tun], [new(40, true)]));
        Assert.Contains("IPv6", TunTakeover.GetError([tun], [new(40, false)]));
        Assert.NotNull(TunTakeover.GetError([tun], [new(2, false), new(2, true)]));
        Assert.Null(TunTakeover.GetError([tun], [new(40, false), new(40, true)]));
    }

    [Fact]
    public void Same_name_without_expected_addresses_or_link_is_not_ready()
    {
        Assert.NotNull(TunTakeover.GetError([Adapter(up: false)], [new(40, false), new(40, true)]));
        Assert.NotNull(TunTakeover.GetError([Adapter(addresses: [])], [new(40, false), new(40, true)]));
    }

    private static NetworkAdapterInfo Adapter(bool up = true, IPAddress[]? addresses = null) => new()
    {
        Identity = new(Guid.NewGuid(), TunTakeover.InterfaceName), Description = "Mock TUN", Luid = 40,
        IfIndex = 40, Ipv6IfIndex = 40, IsUp = up,
        Addresses = addresses ?? [IPAddress.Parse("172.19.0.1"), IPAddress.Parse("fdfe:dcba:9876::1")],
        Gateways = [], DnsServers = [],
    };
}
