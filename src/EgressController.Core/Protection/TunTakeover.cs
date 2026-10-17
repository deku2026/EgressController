using System.Net;
using EgressController.Core.Models;

namespace EgressController.Core.Protection;

// Prefix and metrics are required: /1 routes override /0 regardless of metric.
public sealed record TunDefaultRoute(int InterfaceIndex, bool Ipv6, IPAddress? Destination = null,
    int PrefixLength = 0, uint Metric = 0);

public static class TunTakeover
{
    public const string InterfaceName = "sing-box";

    public static string? GetStartError(IReadOnlyList<NetworkAdapterInfo> adapters,
        IReadOnlyList<TunDefaultRoute> routes, bool allowManagedInterface)
    {
        foreach (NetworkAdapterInfo adapter in adapters.Where(item => item.IsUp))
        {
            bool managedName = adapter.Identity.NameSnapshot.Equals(InterfaceName, StringComparison.OrdinalIgnoreCase);
            if (managedName && allowManagedInterface) continue;
            bool hasRoutes = routes.Any(route => Owns(adapter, route) && IsInternetRoute(route));
            if (managedName || (IsTunnel(adapter) && (hasRoutes || adapter.Addresses.Count > 0)))
                return $"TUN 冲突：接口 {adapter.Identity.NameSnapshot}（{adapter.Description}）已启用，请先关闭对应的 VPN/TUN。";
        }
        return null;
    }

    public static string? GetError(IReadOnlyList<NetworkAdapterInfo> adapters,
        IReadOnlyList<TunDefaultRoute> routes, Guid? expectedInterface = null)
    {
        NetworkAdapterInfo[] matches = adapters.Where(adapter =>
            adapter.Identity.NameSnapshot.Equals(InterfaceName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || !matches[0].IsUp) return "TUN 接口未建立、已断开或身份不唯一";
        NetworkAdapterInfo tun = matches[0];
        if (expectedInterface is { } expected && tun.Identity.Guid != expected)
            return "TUN 接口身份已改变，无法确认由当前 sing-box 接管";
        string? conflict = GetStartError(adapters, routes, allowManagedInterface: true);
        if (conflict is not null) return conflict;
        if (!tun.Addresses.Contains(IPAddress.Parse("172.19.0.1"))
            || !tun.Addresses.Contains(IPAddress.Parse("fdfe:dcba:9876::1"))) return "TUN 接口地址未就绪";
        foreach (bool ipv6 in new[] { false, true })
        {
            var defaults = routes.Where(route => route.Ipv6 == ipv6 && route.PrefixLength == 0 && Owns(tun, route)).ToArray();
            if (defaults.Length == 0) return $"TUN {(ipv6 ? "IPv6" : "IPv4")} 接管路由缺失";
            ulong ownMetric = defaults.Min(route => Metric(tun, route));
            foreach (var route in routes.Where(route => route.Ipv6 == ipv6 && !Owns(tun, route) && IsInternetRoute(route)))
            {
                var owner = adapters.FirstOrDefault(adapter => Owns(adapter, route));
                if (owner is { IsUp: false }) continue;
                // The host's own public addresses are local destinations, not an Internet bypass.
                if (route.PrefixLength == (ipv6 ? 128 : 32) && route.Destination is { } address
                    && adapters.Any(adapter => adapter.Addresses.Contains(address))) continue;
                if (route.PrefixLength > 0 || owner is null || Metric(owner, route) <= ownMetric)
                    return $"TUN 路由冲突：{owner?.Identity.NameSnapshot ?? $"ifIndex {route.InterfaceIndex}"} 的 "
                        + $"{route.Destination?.ToString() ?? (ipv6 ? "::" : "0.0.0.0")}/{route.PrefixLength} 路由可能绕过当前 TUN。";
            }
        }
        return null;
    }

    private static bool Owns(NetworkAdapterInfo adapter, TunDefaultRoute route)
        => route.InterfaceIndex == (route.Ipv6 ? adapter.Ipv6IfIndex : adapter.IfIndex);
    private static ulong Metric(NetworkAdapterInfo adapter, TunDefaultRoute route)
        => (ulong)route.Metric + (route.Ipv6 ? adapter.Ipv6Metric : adapter.Ipv4Metric);

    private static bool IsTunnel(NetworkAdapterInfo adapter)
    {
        if (adapter.InterfaceType is 53 or 131) return true;
        string identity = adapter.Identity.NameSnapshot + " " + adapter.Description;
        return new[] { "wintun", "wireguard", "tap-windows", "tap-win32", "mihomo", "clash", "sing-box", "openvpn" }
            .Any(name => identity.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsInternetRoute(TunDefaultRoute route)
    {
        if (route.PrefixLength == 0 || route.Destination is null) return true;
        (string Address, int Prefix)[] local = route.Ipv6
            ? [("::", 128), ("::1", 128), ("fe80::", 10), ("fc00::", 7), ("ff00::", 8)]
            : [("0.0.0.0", 8), ("10.0.0.0", 8), ("127.0.0.0", 8), ("169.254.0.0", 16),
                ("172.16.0.0", 12), ("192.168.0.0", 16), ("224.0.0.0", 4), ("240.0.0.0", 4)];
        return !local.Any(network => route.PrefixLength >= network.Prefix
            && new IPNetwork(IPAddress.Parse(network.Address), network.Prefix).Contains(route.Destination));
    }
}
