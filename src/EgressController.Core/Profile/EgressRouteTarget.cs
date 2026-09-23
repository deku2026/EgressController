namespace EgressController.Core.Profile;

/// <summary>A saved route follows eSIM, the current default, or one configured SOCKS5 port.</summary>
public sealed record EgressRouteTarget
{
    public string Kind { get; init; } = "esim";
    public int? Port { get; init; }

    public static EgressRouteTarget Esim { get; } = new();
    public static EgressRouteTarget Default { get; } = new() { Kind = "default" };
    public static EgressRouteTarget ForPort(int port) => new() { Kind = "port", Port = port };

    public EgressRouteTarget NormalizeAndValidate(IReadOnlyList<int> ports)
    {
        string kind = (Kind ?? string.Empty).Trim().ToLowerInvariant();
        return kind switch
        {
            "esim" when Port is null => Esim,
            "default" when Port is null => Default,
            "port" when Port is int port && ports.Contains(port) => ForPort(port),
            _ => throw new ArgumentException("分流出口无效，或所选端口不在首页端口列表中。"),
        };
    }
}

public sealed record EgressNamedRoute
{
    public required string Name { get; init; }
    public EgressRouteTarget Target { get; init; } = EgressRouteTarget.Esim;
}
