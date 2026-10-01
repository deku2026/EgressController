using System.Globalization;
using System.Net;
using System.Text.Json.Serialization;

namespace EgressController.Core.Profile;

public static class EgressProfileSchema
{
    public const int CurrentVersion = 4;
    public const string ManagedCore = "managed";
}

public sealed record EgressCoreSelection
{
    public string Mode { get; init; } = EgressProfileSchema.ManagedCore;
}

public sealed record EgressApplicationSelection
{
    public required string DiscoveryKey { get; init; }
    public string? DisplayName { get; init; }
    public IReadOnlyList<string> ExecutablePaths { get; init; } = [];
    public EgressRouteTarget Target { get; init; } = EgressRouteTarget.DefaultAdapter;
}

/// <summary>
/// The only user-editable network configuration. Runtime process IDs, owner paths, interface
/// indexes, source addresses, API secrets and generated config paths intentionally do not belong
/// in this document.
/// </summary>
public sealed record EgressProfileDocument
{
    public int SchemaVersion { get; init; } = EgressProfileSchema.CurrentVersion;
    public EgressCoreSelection Core { get; init; } = new();
    public int UpstreamPort { get; init; } = 7890;
    public IReadOnlyList<int> UpstreamPorts { get; init; } = [7890];
    public IReadOnlyList<EgressAdapterDefinition> Adapters { get; init; } = [];
    public const string DirectAdapterName = "ESIM-家宽";
    public const string ProxyAdapterName = "Proxy-代理";
    public string? DefaultAdapterId { get; init; }
    public string? ProxyAdapterId { get; init; }
    // Legacy override is read for migration; DNS and health checks now always use ESIM-家宽.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DnsAdapterId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrimaryAdapterId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EsimAdapterId { get; init; }
    public IReadOnlyList<EgressApplicationSelection> Applications { get; init; } = [];
    public IReadOnlyList<EgressNamedRoute> RuleSets { get; init; } = [];
    public IReadOnlyList<EgressNamedRoute> Domains { get; init; } = [];

    // Legacy fields are consumed during migration and omitted from schema 4 saves.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<EgressApplicationSelection>? EsimApplications { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? EsimRuleSets { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? EsimDomains { get; init; }

    public static EgressProfileDocument Default { get; } = new();

    public EgressProfileDocument NormalizeAndValidate()
    {
        if (SchemaVersion is not (1 or 2 or 3 or EgressProfileSchema.CurrentVersion))
        {
            throw new ProfileSchemaException(
                $"不支持的 Profile schemaVersion={SchemaVersion}；需要升级 EgressController 后再打开。",
                SchemaVersion);
        }

        if (UpstreamPort is < 1 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(UpstreamPort), UpstreamPort, "上游 SOCKS5 端口必须在 1..65535。 ");

        int[] ports = (SchemaVersion == 1 ? [UpstreamPort] : UpstreamPorts ?? [])
            .Distinct().Order().ToArray();
        if (ports.Any(port => port is < 1 or > ushort.MaxValue))
            throw new ArgumentException("SOCKS5 端口必须在 1-65535。", nameof(UpstreamPorts));
        if (ports.Length > 0 && !ports.Contains(UpstreamPort))
            throw new ArgumentException("默认端口必须在首页端口列表中。", nameof(UpstreamPort));

        EgressCoreSelection core = NormalizeCore(Core);
        string? primary = NormalizeAdapterId(PrimaryAdapterId, nameof(PrimaryAdapterId));
        string? esim = NormalizeAdapterId(EsimAdapterId, nameof(EsimAdapterId));
        var adapters = (Adapters ?? []).Select(adapter => new EgressAdapterDefinition
        {
            Id = NormalizeAdapterId(adapter.Id, nameof(Adapters)) ?? throw new ArgumentException("网卡 ID 不能为空。"),
            Name = string.IsNullOrWhiteSpace(adapter.Name) ? "网卡" : adapter.Name.Trim(),
        }).ToList();
        if (adapters.GroupBy(adapter => adapter.Id).Any(group => group.Count() > 1))
            throw new ArgumentException("不能重复添加同一张网卡。");
        foreach (string legacyId in new[] { esim, primary }.OfType<string>().Distinct())
            if (adapters.All(adapter => adapter.Id != legacyId))
                adapters.Add(new() { Id = legacyId, Name = $"网卡{adapters.Count + 1}" });
        string? defaultAdapter = NormalizeAdapterId(DefaultAdapterId, nameof(DefaultAdapterId)) ?? esim ?? primary;
        string? proxyAdapter = NormalizeAdapterId(ProxyAdapterId, nameof(ProxyAdapterId));
        if (SchemaVersion < 4)
        {
            // v1/2 had an explicit primary uplink. v3 may have more than two adapters;
            // never guess which unrelated third interface should become the proxy uplink.
            proxyAdapter ??= primary;
            if (proxyAdapter == defaultAdapter) proxyAdapter = null;
            if (proxyAdapter is null && adapters.Count == 2)
                proxyAdapter = adapters.FirstOrDefault(adapter => adapter.Id != defaultAdapter)?.Id;
        }
        if (defaultAdapter is not null && defaultAdapter == proxyAdapter)
            throw new ArgumentException("ESIM-家宽 和 Proxy-代理 必须选择两张不同的网卡。");
        // Unknown legacy rule IDs remain explicit and block readiness until reassigned.
        // This preserves selections without silently routing them through a different card.
        adapters = new[]
        {
            defaultAdapter is null ? null : new EgressAdapterDefinition { Id = defaultAdapter, Name = DirectAdapterName },
            proxyAdapter is null ? null : new EgressAdapterDefinition { Id = proxyAdapter, Name = ProxyAdapterName },
        }.OfType<EgressAdapterDefinition>().ToList();
        EgressRouteTarget Migrate(EgressRouteTarget target)
        {
            if (target.Kind == "esim" && (target.Port is not null || target.AdapterId is not null))
                throw new ArgumentException("旧版 eSIM 出口包含无效字段。");
            return target.Kind == "esim" || (SchemaVersion < 3 && target == EgressRouteTarget.DefaultAdapter)
                ? (esim is null ? EgressRouteTarget.DefaultAdapter : EgressRouteTarget.ForAdapter(esim)) : target;
        }

        var applications = (Applications ?? []).Concat((EsimApplications ?? []).Select(value => value with { Target = esim is null ? EgressRouteTarget.DefaultAdapter : EgressRouteTarget.ForAdapter(esim) }))
            .Select(value => NormalizeApplication(value with { Target = Migrate(value.Target) }, ports))
            .GroupBy(x => x.DiscoveryKey, StringComparer.Ordinal)
            .Select(group => UniqueRoute(group, value => value.Target))
            .OrderBy(x => x.DiscoveryKey, StringComparer.Ordinal)
            .ToArray();

        EgressNamedRoute[] ruleSets = NormalizeRoutes(RuleSets?.Select(route => route with { Target = Migrate(route.Target) }).ToArray(), EsimRuleSets, NormalizeRuleSetName, ports, esim);
        EgressNamedRoute[] domains = NormalizeRoutes(Domains?.Select(route => route with { Target = Migrate(route.Target) }).ToArray(), EsimDomains, NormalizeDomain, ports, esim);

        return this with
        {
            Adapters = adapters.ToArray(),
            DefaultAdapterId = defaultAdapter,
            ProxyAdapterId = proxyAdapter,
            DnsAdapterId = null,
            SchemaVersion = EgressProfileSchema.CurrentVersion,
            Core = core,
            UpstreamPorts = ports,
            PrimaryAdapterId = null,
            EsimAdapterId = null,
            Applications = applications,
            RuleSets = ruleSets,
            Domains = domains,
            EsimApplications = null,
            EsimRuleSets = null,
            EsimDomains = null,
        };
    }

    [JsonIgnore]
    public string? EffectiveDnsAdapterId => DefaultAdapterId;

    [JsonIgnore]
    public string? AdapterConfigurationError
    {
        get
        {
            if (DefaultAdapterId is null) return "请选择 ESIM-家宽 的实际网卡。";
            if (ProxyAdapterId is null) return "请选择 Proxy-代理 的实际网卡。";
            bool Selected(string? id) => id is null || id == DefaultAdapterId || id == ProxyAdapterId;
            if (Applications.Select(item => item.Target).Concat(RuleSets.Select(item => item.Target))
                .Concat(Domains.Select(item => item.Target)).Any(target => target.IsAdapter && !Selected(target.AdapterId)))
                return "部分规则仍绑定旧网卡，请在应用或域名规则中重新选择两个网卡出口。";
            return null;
        }
    }

    /// <summary>Replace either role atomically and carry its explicit application references with it.</summary>
    public EgressProfileDocument SetAdapterRoles(string? directId, string? proxyId)
    {
        directId = NormalizeAdapterId(directId, nameof(directId));
        proxyId = NormalizeAdapterId(proxyId, nameof(proxyId));
        if (directId is not null && directId == proxyId)
            throw new ArgumentException("两个角色不能选择同一张网卡；可以使用交换网卡。");
        string? Replace(string? id) => id is null ? null
            : id == DefaultAdapterId ? directId : id == ProxyAdapterId ? proxyId : id;
        EgressRouteTarget Route(EgressRouteTarget target) => target.Kind == "adapter" && Replace(target.AdapterId) is string id
            ? EgressRouteTarget.ForAdapter(id) : target;
        return (this with
        {
            DefaultAdapterId = directId, ProxyAdapterId = proxyId, DnsAdapterId = null,
            Applications = Applications.Select(item => item with { Target = Route(item.Target) }).ToArray(),
            RuleSets = RuleSets.Select(item => item with { Target = Route(item.Target) }).ToArray(),
            Domains = Domains.Select(item => item with { Target = Route(item.Target) }).ToArray(),
        }).NormalizeAndValidate();
    }

    public EgressProfileDocument AddPort(int port)
        => (this with { UpstreamPorts = UpstreamPorts.Append(port).ToArray(), UpstreamPort = UpstreamPorts.Count == 0 ? port : UpstreamPort }).NormalizeAndValidate();

    public EgressProfileDocument SetDefaultPort(int port)
    {
        if (!UpstreamPorts.Contains(port)) throw new ArgumentException("默认端口必须在端口列表中。");
        return (this with { UpstreamPort = port }).NormalizeAndValidate();
    }

    public string? PortRemovalError(int port)
    {
        if (port == UpstreamPort && UpstreamPorts.Count > 1)
            return "这是默认端口，请先将其他端口设为默认。";
        if (Applications.Select(route => route.Target)
            .Concat(RuleSets.Select(route => route.Target))
            .Concat(Domains.Select(route => route.Target))
            .Any(target => target.Port == port || (target.Kind == "default" && port == UpstreamPort)))
            return "这个端口仍被分流规则使用，请先修改相应规则的出口。";
        return null;
    }

    public EgressProfileDocument RemovePort(int port)
    {
        if (PortRemovalError(port) is string error)
            throw new ArgumentException(error, nameof(port));
        return (this with { UpstreamPorts = UpstreamPorts.Where(value => value != port).ToArray() })
            .NormalizeAndValidate();
    }

    private static EgressNamedRoute[] NormalizeRoutes(
        IReadOnlyList<EgressNamedRoute>? routes,
        IReadOnlyList<string>? legacy,
        Func<string, string> normalizeName,
        IReadOnlyList<int> ports, string? legacyAdapterId)
        => (routes ?? []).Concat((legacy ?? []).Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => new EgressNamedRoute { Name = value, Target = legacyAdapterId is null ? EgressRouteTarget.DefaultAdapter : EgressRouteTarget.ForAdapter(legacyAdapterId) }))
            .Select(route => new EgressNamedRoute
            {
                Name = normalizeName(route?.Name ?? throw new ArgumentException("分流规则不能为空。")),
                Target = (route.Target ?? throw new ArgumentException("分流出口不能为空。"))
                    .NormalizeAndValidate(ports),
            })
            .GroupBy(route => route.Name, StringComparer.Ordinal)
            .Select(group => UniqueRoute(group, route => route.Target))
            .OrderBy(route => route.Name, StringComparer.Ordinal)
            .ToArray();

    private static T UniqueRoute<T>(IGrouping<string, T> group, Func<T, EgressRouteTarget> target)
    {
        if (group.Select(target).Distinct().Skip(1).Any())
            throw new ArgumentException($"同一条规则不能指定不同出口：{group.Key}");
        return group.First();
    }

    public static string NormalizeDomain(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("域名不能为空。", nameof(value));

        string input = value.Trim().TrimEnd('.');
        if (input.Length == 0 || input.Contains('/') || input.Contains('\\') || input.Contains('@')
            || input.Contains(':') || input.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException($"非法域名：{value}", nameof(value));
        }

        string ascii;
        try
        {
            ascii = new IdnMapping().GetAscii(input);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"非法域名：{value}", nameof(value), ex);
        }

        if (ascii.Length > 253 || IPAddress.TryParse(ascii, out _)
            || Uri.CheckHostName(ascii) != UriHostNameType.Dns)
        {
            throw new ArgumentException($"非法域名：{value}", nameof(value));
        }

        foreach (string label in ascii.Split('.'))
        {
            if (label.Length is < 1 or > 63 || label[0] == '-' || label[^1] == '-'
                || label.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
            {
                throw new ArgumentException($"非法域名：{value}", nameof(value));
            }
        }

        return ascii.ToLowerInvariant();
    }

    private static EgressCoreSelection NormalizeCore(EgressCoreSelection? value)
    {
        EgressCoreSelection core = value ?? new EgressCoreSelection();
        string mode = (core.Mode ?? string.Empty).Trim().ToLowerInvariant();
        if (mode != EgressProfileSchema.ManagedCore)
            throw new ArgumentException("Core 只能使用由 EgressController 管理的 sing-box。", nameof(Core));

        return new EgressCoreSelection { Mode = EgressProfileSchema.ManagedCore };
    }

    private static EgressApplicationSelection NormalizeApplication(EgressApplicationSelection? value, IReadOnlyList<int> ports)
    {
        if (value is null || string.IsNullOrWhiteSpace(value.DiscoveryKey))
            throw new ArgumentException("应用 DiscoveryKey 不能为空。", nameof(Applications));

        return new EgressApplicationSelection
        {
            DiscoveryKey = value.DiscoveryKey.Trim(),
            DisplayName = value.DisplayName,
            ExecutablePaths = (value.ExecutablePaths ?? []).Where(path => Path.IsPathFullyQualified(path)
                && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Target = (value.Target ?? throw new ArgumentException("应用分流出口不能为空。"))
                .NormalizeAndValidate(ports),
        };
    }

    private static string? NormalizeAdapterId(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (!Guid.TryParse(value.Trim(), out Guid guid) || guid == Guid.Empty)
            throw new ArgumentException("网卡 ID 必须是稳定 GUID。", parameterName);
        return guid.ToString("D", CultureInfo.InvariantCulture);
    }

    private static string NormalizeRuleSetName(string value)
    {
        string name = value.Trim().Replace('\\', '/').TrimStart('/');
        if (name.Length == 0 || name.Contains("..", StringComparison.Ordinal)
            || name.Any(char.IsWhiteSpace)
            || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or '@' or '!')))
        {
            throw new ArgumentException($"非法规则集名称：{value}", nameof(RuleSets));
        }
        return name.ToLowerInvariant();
    }
}

public sealed class ProfileSchemaException(string message, int schemaVersion) : InvalidOperationException(message)
{
    public int SchemaVersion { get; } = schemaVersion;
}
