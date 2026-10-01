using System.Net;
using EgressController.Core.Models;

namespace EgressController.Core.Protection;

public sealed record TunDefaultRoute(int InterfaceIndex, bool Ipv6);

/// <summary>Read-only local evidence; no Internet probes or adapter writes.</summary>
public static class TunTakeover
{
    public const string InterfaceName = "sing-box";
    public static string? GetError(IReadOnlyList<NetworkAdapterInfo> adapters, IReadOnlyList<TunDefaultRoute> routes)
    {
        NetworkAdapterInfo? tun = adapters.FirstOrDefault(adapter =>
            string.Equals(adapter.Identity.NameSnapshot, InterfaceName, StringComparison.OrdinalIgnoreCase));
        if (tun is null || !tun.IsUp) return "TUN 接口未建立或已断开";
        if (!tun.Addresses.Contains(IPAddress.Parse("172.19.0.1"))
            || !tun.Addresses.Contains(IPAddress.Parse("fdfe:dcba:9876::1"))) return "TUN 接口地址未就绪";
        if (!routes.Any(route => !route.Ipv6 && route.InterfaceIndex == tun.IfIndex)) return "TUN IPv4 接管路由缺失";
        if (!routes.Any(route => route.Ipv6 && route.InterfaceIndex == tun.Ipv6IfIndex)) return "TUN IPv6 接管路由缺失";
        return null;
    }
}
