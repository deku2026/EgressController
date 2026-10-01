using EgressController.Core.Models;
using EgressController.Core.Profile;

namespace EgressController.Windows.Network;

public sealed class NetworkEnvironmentResolver
{
    public NetworkEnvironmentSnapshot Resolve(EgressProfileDocument profile, IReadOnlyList<NetworkAdapterInfo> adapters)
    {
        profile = profile.NormalizeAndValidate();
        AdapterSelection ResolveOne(string? id)
        {
            NetworkAdapterInfo? found = adapters.FirstOrDefault(adapter => adapter.Identity.Guid.ToString("D") == id);
            if (found is not null && !IsSelectable(found))
                throw new NetworkEnvironmentException($"网卡 {found.Identity.NameSnapshot} 不能作为出口。", "adapter.invalid");
            return found is null ? Unavailable(id) : ToSelection(found);
        }
        return new()
        {
            DefaultAdapter = ResolveOne(profile.DefaultAdapterId),
            ProxyAdapter = ResolveOne(profile.ProxyAdapterId),
            DnsAdapter = profile.EffectiveDnsAdapterId == profile.DefaultAdapterId || profile.EffectiveDnsAdapterId == profile.ProxyAdapterId
                ? ResolveOne(profile.EffectiveDnsAdapterId) : Unavailable(profile.EffectiveDnsAdapterId),
            Adapters = profile.Adapters.Select(adapter => ResolveOne(adapter.Id)).ToArray(),
            CapturedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    public static AdapterSelection ToSelection(NetworkAdapterInfo adapter) => new()
    {
        AdapterId = adapter.Identity.Guid, Alias = adapter.Identity.NameSnapshot,
        Luid = adapter.Luid, IfIndex = adapter.IfIndex, Ipv6IfIndex = adapter.Ipv6IfIndex,
        IsUp = adapter.IsUp, AddressState = adapter.AddressState,
        Ipv4BindAddress = adapter.Ipv4BindAddress, Ipv6BindAddress = adapter.Ipv6BindAddress,
    };

    public static bool IsRecommended(NetworkAdapterInfo adapter)
    {
        if (!IsSelectable(adapter) || !adapter.IsUp || !ToSelection(adapter).IsReady) return false;
        string description = $"{adapter.Identity.NameSnapshot} {adapter.Description}";
        string[] internalInterfaces = ["hyper-v", "vethernet", "vmware", "virtualbox", "wsl", "docker", "host-only", "wi-fi direct", "bluetooth"];
        return adapter.InterfaceType is 6 or 71 or 243 or 244
            && !internalInterfaces.Any(name => description.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsSelectable(NetworkAdapterInfo adapter)
    {
        if (adapter.Identity.Guid == Guid.Empty || adapter.InterfaceType is 24 or 131) return false;
        string name = $"{adapter.Identity.NameSnapshot} {adapter.Description}";
        return !name.Contains("loopback", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("wintun", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("sing-box", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("egresscontroller", StringComparison.OrdinalIgnoreCase);
    }

    private static AdapterSelection Unavailable(string? id) => new()
    {
        AdapterId = Guid.TryParse(id, out Guid guid) ? guid : Guid.Empty,
        Alias = "网卡未连接", Luid = 0, IfIndex = 0, Ipv6IfIndex = 0,
        IsUp = false, AddressState = AdapterAddressState.NoAddress,
    };
}

public sealed class NetworkEnvironmentException(string message, string code) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
