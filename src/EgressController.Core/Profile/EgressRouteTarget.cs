namespace EgressController.Core.Profile;

/// <summary>Adapter and SOCKS5 destinations have independent defaults.</summary>
public sealed record EgressRouteTarget
{
    public string Kind { get; init; } = "adapter-default";
    public int? Port { get; init; }
    public string? AdapterId { get; init; }
    public static EgressRouteTarget DefaultAdapter { get; } = new();
    public static EgressRouteTarget Default { get; } = new() { Kind = "default" };
    public static EgressRouteTarget ForPort(int port) => new() { Kind = "port", Port = port };
    public static EgressRouteTarget ForAdapter(string id) => new() { Kind = "adapter", AdapterId = id };
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsAdapter => Kind is "adapter" or "adapter-default";

    public EgressRouteTarget NormalizeAndValidate(IReadOnlyList<int> ports)
        => (Kind ?? "").Trim().ToLowerInvariant() switch
        {
            "adapter-default" when Port is null && AdapterId is null => DefaultAdapter,
            "adapter" when Port is null && Guid.TryParse(AdapterId, out Guid id) && id != Guid.Empty
                => ForAdapter(id.ToString("D")),
            "default" when Port is null && AdapterId is null && ports.Count > 0 => Default,
            "port" when AdapterId is null && Port is int port && ports.Contains(port) => ForPort(port),
            _ => throw new ArgumentException("分流出口无效，或所选端口不在端口列表中。"),
        };
}

public sealed record EgressNamedRoute
{
    public required string Name { get; init; }
    public EgressRouteTarget Target { get; init; } = EgressRouteTarget.DefaultAdapter;
}
