using System.Text.Json.Serialization;

namespace EgressController.Core.Routing;

/// <summary>Portable configuration. Windows schema-4 remains its authoritative persisted inventory.</summary>
public sealed record RoutingProfile
{
    public int SchemaVersion { get; init; } = 1;
    public string Platform { get; init; } = "macos";
    public AdapterSelection[] Adapters { get; init; } = [];
    public LocalProxy[] Ports { get; init; } = [];
    public ApplicationRoute[] Applications { get; init; } = [];
    public ApplicationMetadata[] ApplicationMetadata { get; init; } = [];
    public DomainRoute[] Domains { get; init; } = [];
    public RuleSetRoute[] RuleSets { get; init; } = [];
    public RouteDestination DefaultRoute { get; init; } = new();
    public int? DefaultDownloadPort { get; init; }
}
public sealed record RouteDestination
{
    public string Kind { get; init; } = "direct";
    public string Id { get; init; } = "";
    public static RouteDestination Adapter(string id) => new() { Kind = "adapter", Id = id };
    public static RouteDestination Socks(int port) => new() { Kind = "socks5", Id = port.ToString(System.Globalization.CultureInfo.InvariantCulture) };
}
public sealed record AdapterSelection(string Id, string Label);
public sealed record ResolvedAdapter(string Id, string Name, string InterfaceName, bool Available, string Address);
public sealed record LocalProxy(int Port, string Name, string OwnerPath, string EgressAdapterId);
public sealed record ApplicationRoute(string Id, string Name, string Path, bool Bundle, RouteDestination Target, string Category = "未分类");
public sealed record ApplicationMetadata(string Path, string Category);
public sealed record DomainRoute(string Domain, RouteDestination Target);
public sealed record RuleSetRoute(string Id, string Name, string Url, RouteDestination Target);

[JsonSerializable(typeof(RoutingProfile))]
public sealed partial class RoutingJsonContext : JsonSerializerContext;
