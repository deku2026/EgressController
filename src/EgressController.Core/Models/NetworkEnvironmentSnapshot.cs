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

    public bool IsReady => AdapterId != Guid.Empty && IsUp && !string.IsNullOrWhiteSpace(Alias) && (HasIpv4 || HasIpv6);

    public bool HasIpv4 => Ipv4BindAddress is not null;
    public bool HasIpv6 => Ipv6BindAddress is not null;
}

public sealed record NetworkEnvironmentSnapshot
{
    public IReadOnlyList<AdapterSelection> Adapters { get; init; } = [];
    public required AdapterSelection DefaultAdapter { get; init; }
    public required AdapterSelection ProxyAdapter { get; init; }
    public required AdapterSelection DnsAdapter { get; init; }
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public IEnumerable<AdapterSelection> AllAdapters => Adapters.Concat([DefaultAdapter, ProxyAdapter, DnsAdapter]).DistinctBy(adapter => adapter.AdapterId);
    public AdapterSelection? Find(string? id) => AllAdapters.FirstOrDefault(adapter => adapter.AdapterId.ToString("D") == id);
    public bool IsDnsReady => DnsAdapter.IsReady;
    public bool IsDualStack => DefaultAdapter.HasIpv4 && DefaultAdapter.HasIpv6 && DnsAdapter.HasIpv4 && DnsAdapter.HasIpv6;
}
