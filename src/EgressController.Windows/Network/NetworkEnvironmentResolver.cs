using EgressController.Core.Models;
using EgressController.Core.Profile;

namespace EgressController.Windows.Network;

public sealed class NetworkEnvironmentResolver
{
    public NetworkEnvironmentSnapshot Resolve(
        EgressProfileDocument profile,
        IReadOnlyList<NetworkAdapterInfo> adapters)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(adapters);

        Guid primaryId = ParseRequiredAdapterId(profile.PrimaryAdapterId, "主网卡");
        Guid? esimId = ParseOptionalAdapterId(profile.EsimAdapterId, "eSIM 网卡");
        if (primaryId == esimId)
            throw new NetworkEnvironmentException("主网卡和 eSIM 网卡不能是同一个接口。", "adapter.same");

        NetworkAdapterInfo primary = FindAdapter(adapters, primaryId, "主网卡");
        NetworkAdapterInfo? esim = esimId is Guid selectedEsim
            ? FindAdapter(adapters, selectedEsim, "eSIM 网卡")
            : null;
        if (!IsSelectable(primary))
            throw new NetworkEnvironmentException("选中的主网卡不是可用的物理出口。", "primary.invalid");
        if (esim is not null && !IsSelectable(esim))
            throw new NetworkEnvironmentException("选中的 eSIM 网卡不是可用的物理出口。", "esim.invalid");

        return new NetworkEnvironmentSnapshot
        {
            Primary = ToSelection(primary),
            Esim = esim is null ? UnavailableEsim() : ToSelection(esim),
            CapturedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    public static AdapterSelection ToSelection(NetworkAdapterInfo adapter)
        => new()
        {
            AdapterId = adapter.Identity.Guid,
            Alias = adapter.Identity.NameSnapshot,
            Luid = adapter.Luid,
            IfIndex = adapter.IfIndex,
            Ipv6IfIndex = adapter.Ipv6IfIndex,
            IsUp = adapter.IsUp,
            AddressState = adapter.AddressState,
            Ipv4BindAddress = adapter.Ipv4BindAddress,
            Ipv6BindAddress = adapter.Ipv6BindAddress,
        };

    /// <summary>Returns a profile with safe automatic defaults for first-run TUN startup.</summary>
    public static EgressProfileDocument EnsureAutomaticDefaults(
        EgressProfileDocument profile,
        IReadOnlyList<NetworkAdapterInfo> adapters)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(adapters);

        NetworkAdapterInfo[] selectable = adapters.Where(IsSelectable).ToArray();
        Guid? primaryId = ParseOptionalAdapterId(profile.PrimaryAdapterId, "主网卡");
        if (primaryId is null || selectable.All(adapter => adapter.Identity.Guid != primaryId.Value))
        {
            NetworkAdapterInfo? primary = selectable
                .Where(adapter => adapter.IsUp && (adapter.Ipv4BindAddress is not null || adapter.Ipv6BindAddress is not null))
                .Where(adapter => !IsLikelyEsim(adapter))
                .OrderBy(adapter => adapter.Identity.NameSnapshot, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault()
                ?? selectable
                    .Where(adapter => adapter.IsUp && (adapter.Ipv4BindAddress is not null || adapter.Ipv6BindAddress is not null))
                    .OrderBy(adapter => adapter.Identity.NameSnapshot, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
            primaryId = primary?.Identity.Guid;
        }

        Guid? esimId = ParseOptionalAdapterId(profile.EsimAdapterId, "eSIM 网卡");
        if (esimId is not null
            && (selectable.All(adapter => adapter.Identity.Guid != esimId.Value) || esimId == primaryId))
            esimId = null;
        if (esimId is null && primaryId is Guid selectedPrimary)
        {
            esimId = selectable
                .Where(adapter => adapter.Identity.Guid != selectedPrimary && IsLikelyEsim(adapter))
                .OrderBy(adapter => adapter.Identity.NameSnapshot, StringComparer.OrdinalIgnoreCase)
                .Select(adapter => (Guid?)adapter.Identity.Guid)
                .FirstOrDefault();
        }

        return profile with
        {
            PrimaryAdapterId = primaryId?.ToString("D"),
            EsimAdapterId = esimId?.ToString("D"),
        };
    }

    public static bool IsLikelyEsim(NetworkAdapterInfo adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        string text = $"{adapter.Identity.NameSnapshot} {adapter.Description}";
        return new[] { "esim", "cellular", "mobile", "wwan", "lte", "4g", "5g", "modem", "蜂窝", "移动", "手机" }
            .Any(token => text.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static NetworkAdapterInfo FindAdapter(
        IReadOnlyList<NetworkAdapterInfo> adapters,
        Guid id,
        string label)
        => adapters.FirstOrDefault(adapter => adapter.Identity.Guid == id)
            ?? throw new NetworkEnvironmentException($"找不到已选择的{label}，请重新扫描网卡。", $"{label}.missing");

    private static Guid ParseRequiredAdapterId(string? value, string label)
        => Guid.TryParse(value, out Guid id) && id != Guid.Empty
            ? id
            : throw new NetworkEnvironmentException($"尚未选择{label}。", $"{label}.unselected");

    private static Guid? ParseOptionalAdapterId(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return Guid.TryParse(value, out Guid id) && id != Guid.Empty
            ? id
            : throw new NetworkEnvironmentException($"{label} ID 无效。", $"{label}.invalid");
    }

    public static bool IsSelectable(NetworkAdapterInfo adapter)
    {
        if (adapter.Identity.Guid == Guid.Empty || adapter.InterfaceType is 24 or 131)
            return false;
        string name = $"{adapter.Identity.NameSnapshot} {adapter.Description}";
        return !name.Contains("loopback", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("wintun", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("tap-", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("tun", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("virtualbox", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("vmware", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("hyper-v", StringComparison.OrdinalIgnoreCase);
    }

    private static AdapterSelection UnavailableEsim()
        => new()
        {
            AdapterId = Guid.Empty,
            Alias = "eSIM unavailable",
            Luid = 0,
            IfIndex = 0,
            Ipv6IfIndex = 0,
            IsUp = false,
            AddressState = AdapterAddressState.NoAddress,
        };
}

public sealed class NetworkEnvironmentException(string message, string code) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
