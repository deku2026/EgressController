using EgressController.Core.Models;
using EgressController.Core.Profile;

namespace EgressController.App.ViewModels;

public static class RouteNames
{
    public static string Adapter(string? id, EgressProfileDocument profile, IReadOnlyList<NetworkAdapterInfo> adapters, bool details = false)
    {
        var adapter = adapters.FirstOrDefault(item => Guid.TryParse(id, out var guid) && item.Identity.Guid == guid);
        if (adapter is not null)
            return adapter.Identity.NameSnapshot + (details && !string.IsNullOrWhiteSpace(adapter.Description) ? " · " + adapter.Description : "")
                + (adapter.IsUp ? "" : " · 已断开");
        return (profile.Adapters.FirstOrDefault(item => item.Id == id)?.Name ?? "网卡") + " · 未连接";
    }
    public static string Outbound(string? tag, EgressProfileDocument profile, IReadOnlyList<NetworkAdapterInfo> adapters, bool details = false)
    {
        if (tag is null) return "未知出口";
        if (tag.StartsWith("adapter-", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(tag[8..], out var id))
            return Adapter(id.ToString("D"), profile, adapters, details);
        if (tag.StartsWith("upstream-", StringComparison.OrdinalIgnoreCase) && int.TryParse(tag[9..], out int port)) return $"代理 · 127.0.0.1:{port}";
        return tag switch
        {
            "proxy-direct" => Adapter(profile.ProxyAdapterId, profile, adapters, details),
            "dns-direct" => Adapter(profile.DefaultAdapterId, profile, adapters, details) + " · DNS",
            "recovery-direct" => "恢复通信", "block" or "reject" => "拒绝连接",
            _ => tag,
        };
    }
}
