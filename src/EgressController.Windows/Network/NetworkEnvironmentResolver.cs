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
            DnsAdapter = ResolveOne(profile.EffectiveDnsAdapterId),
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

    public static EgressProfileDocument EnsureAutomaticDefaults(EgressProfileDocument profile, IReadOnlyList<NetworkAdapterInfo> adapters)
    {
        profile = profile.NormalizeAndValidate();
        // An absent saved interface is never silently replaced by another network.
        if (profile.DefaultAdapterId is not null || profile.Adapters.Count > 0) return profile;
        NetworkAdapterInfo? first = adapters.Where(IsSelectable)
            .Where(adapter => adapter.IsUp && (adapter.Ipv4BindAddress is not null || adapter.Ipv6BindAddress is not null))
            .OrderBy(adapter => adapter.Identity.NameSnapshot, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        return first is null ? profile : profile.AddAdapter(first.Identity.Guid.ToString("D"), "网卡1 · " + first.Identity.NameSnapshot);
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
