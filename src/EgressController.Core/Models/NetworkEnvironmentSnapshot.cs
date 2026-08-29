using System.Net;

namespace EgressController.Core.Models;

/// <summary>Resolved runtime binding for one user-selected Windows adapter.</summary>
public sealed record AdapterSelection
{
    public required Guid AdapterId { get; init; }
    public required string Alias { get; init; }
    public required ulong Luid { get; init; }
    public required int IfIndex { get; init; }
    public required int Ipv6IfIndex { get; init; }
    public required bool IsUp { get; init; }
    public required AdapterAddressState AddressState { get; init; }
    public IPAddress? Ipv4BindAddress { get; init; }
    public IPAddress? Ipv6BindAddress { get; init; }

    public bool HasIpv4 => Ipv4BindAddress is not null;
    public bool HasIpv6 => Ipv6BindAddress is not null;
}

public sealed record NetworkEnvironmentSnapshot
{
    public required AdapterSelection Primary { get; init; }
    public required AdapterSelection Esim { get; init; }
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public bool IsDualStack => Primary.HasIpv4 && Primary.HasIpv6 && Esim.HasIpv4 && Esim.HasIpv6;

    /// <summary>
    /// Whether the selected eSIM interface can currently be used as an Internet-bound direct
    /// exit. A missing/offline eSIM is valid for TUN startup; callers must fail closed for rules
    /// assigned to eSIM instead of falling back to another interface.
    /// </summary>
    public bool IsEsimReady
        => Esim.AdapterId != Guid.Empty
            && !string.IsNullOrWhiteSpace(Esim.Alias)
            && Esim.IsUp
            && (Esim.HasIpv4 || Esim.HasIpv6);
}
