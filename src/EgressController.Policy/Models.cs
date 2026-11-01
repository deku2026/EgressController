namespace EgressController.Policy;

public enum ExitKind { Home, NonHome, Proxy }
public enum DiagnosticSeverity { Warning, Error }
public enum GroupKind { Select, UrlTest, Fallback, Unsupported }
public enum NodeProtocol { Vless, Vmess, Shadowsocks, Trojan, Socks, Http, Unsupported }
public enum DomainMatchKind { Full, Suffix, Keyword, Regex }

public sealed record PolicyDiagnostic(string Code, string Message, DiagnosticSeverity Severity = DiagnosticSeverity.Error, string? ObjectId = null);
public sealed record ExitChoice(ExitKind Kind, string? TargetId = null)
{
    public static ExitChoice Home { get; } = new(ExitKind.Home);
    public static ExitChoice NonHome { get; } = new(ExitKind.NonHome);
    public static ExitChoice Proxy(string id) => new(ExitKind.Proxy, id);
}

public sealed class MacPolicyDocument
{
    public int Version { get; set; } = 1;
    public InterfaceSelection Interfaces { get; set; } = new();
    public DefaultPolicy Defaults { get; set; } = new();
    public DnsPolicy Dns { get; set; } = new();
    public TunPolicy Tun { get; set; } = new();
    public List<ManagedApplication> Applications { get; set; } = [];
    public List<ManagedRuleSet> RuleSets { get; set; } = [];
    public List<ProxyNode> Nodes { get; set; } = [];
    public List<ProxyGroup> Groups { get; set; } = [];
    public List<ProxyProvider> Providers { get; set; } = [];
    public List<SubscriptionSource> Subscriptions { get; set; } = [];
}

public sealed class DnsPolicy
{
    public string UpstreamIp { get; set; } = DnsEndpoint.DefaultIp;
    public string HttpsUrl { get; set; } = DnsEndpoint.DefaultHttpsUrl;
}
public sealed class TunPolicy
{
    public string Name { get; set; } = "utun10";
    public string Ipv4Cidr { get; set; } = "198.18.0.1/30";
    public int Mtu { get; set; } = 1500;
}

public sealed class InterfaceSelection
{
    public string HomeId { get; set; } = "";
    public string NonHomeId { get; set; } = "";
}
public sealed class DefaultPolicy
{
    public ExitChoice China { get; set; } = ExitChoice.NonHome;
    public ExitChoice Global { get; set; } = ExitChoice.Proxy("");
    public string ProxyInterface { get; set; } = "nonhome";
    public int ResourceProxyPort { get; set; } = 17890;
}
public sealed class ManagedApplication
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string BundlePath { get; set; } = "";
    public string? BundleIdentifier { get; set; }
    public string? PrimaryExecutable { get; set; }
    // Discovery roots are never emitted as runtime wildcard rules.
    public List<string> ExecutablePaths { get; set; } = [];
    public ExitChoice Via { get; set; } = ExitChoice.NonHome;
    public bool Enabled { get; set; } = true;
}
public sealed class ManagedRuleSet
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Sources { get; set; } = [];
    public List<DomainRule> Domains { get; set; } = [];
    public List<string> IpCidrs { get; set; } = [];
    public ExitChoice Via { get; set; } = ExitChoice.NonHome;
    public bool Enabled { get; set; } = true;
}
public sealed record DomainRule(DomainMatchKind Kind, string Value);
public sealed class SubscriptionSource
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
}

// Classes deliberately retain the default, non-secret-bearing ToString implementation.
public sealed class ProxyNode
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string SourceId { get; set; } = "";
    public NodeProtocol Protocol { get; set; }
    public string Server { get; set; } = "";
    public int Port { get; set; }
    public string UserId { get; set; } = "";
    public string Password { get; set; } = "";
    public string Username { get; set; } = "";
    public string Cipher { get; set; } = "";
    public string Encryption { get; set; } = "none";
    public string Flow { get; set; } = "";
    public string Security { get; set; } = "auto";
    public string Network { get; set; } = "tcp";
    public bool Tls { get; set; }
    public bool SkipCertificateVerify { get; set; }
    public string ServerName { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string RealityPublicKey { get; set; } = "";
    public string RealityShortId { get; set; } = "";
    public string RealitySpiderX { get; set; } = "";
    public List<string> Alpn { get; set; } = [];
    public string TransportPath { get; set; } = "";
    public string TransportHost { get; set; } = "";
    public string GrpcServiceName { get; set; } = "";
    public string Plugin { get; set; } = "";
    public string PluginMode { get; set; } = "";
    public string PluginHost { get; set; } = "";
    public bool? TcpFastOpen { get; set; }
    public string? DialerProxyId { get; set; }
    public List<PolicyDiagnostic> Diagnostics { get; set; } = [];
    public bool IsSupported => Protocol != NodeProtocol.Unsupported && !Diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error);
}
public sealed class ProxyGroup
{
    public bool Retained { get; set; } = true;
    public bool StrategyOverridden { get; set; }
    public string Icon { get; set; } = "";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string SourceId { get; set; } = "";
    public GroupKind Kind { get; set; }
    public List<string> MemberIds { get; set; } = [];
    public List<string> ProviderIds { get; set; } = [];
    public string? SelectedMemberId { get; set; }
    public string TestUrl { get; set; } = "https://www.gstatic.com/generate_204";
    public int IntervalSeconds { get; set; } = 300;
    public int ToleranceMilliseconds { get; set; } = 50;
    public List<PolicyDiagnostic> Diagnostics { get; set; } = [];
    public bool IsSupported => Kind != GroupKind.Unsupported && !Diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error);
}
public sealed class ProxyProvider
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string Url { get; set; } = "";
    public List<PolicyDiagnostic> Diagnostics { get; set; } = [];
}

public sealed record PolicyReadResult(MacPolicyDocument? Document, IReadOnlyList<PolicyDiagnostic> Diagnostics)
{
    public bool IsSuccess => Document is not null && !Diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error);
}
public sealed record ClashImportResult(IReadOnlyList<ProxyNode> Nodes, IReadOnlyList<ProxyGroup> Groups, IReadOnlyList<ProxyProvider> Providers, IReadOnlyList<PolicyDiagnostic> Diagnostics)
{
    // Unsupported individual items are retained for display and cannot be selected at compilation.
    public bool IsSuccess => (Nodes.Count > 0 || Providers.Count > 0) && !Diagnostics.Any(x => x.Code is "subscription.syntax" or "subscription.id" or "subscription.duplicate-name");
}
public sealed record InterfaceBinding(string StableId, string BsdName);
public sealed record RuleSourceBinding(string Source, string Tag, string? DomainFile = null, string? IpFile = null);
public sealed record GroupProbe(string NodeId, bool Available, double LatencyMilliseconds, DateTimeOffset ObservedAt, string? TestUrl = null, string? FailureCode = null);
public sealed record GroupSelectionResult(string? NodeId, IReadOnlyList<PolicyDiagnostic> Diagnostics)
{
    public bool IsSuccess => NodeId is not null && !Diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error);
}
public sealed class XrayCompileOptions
{
    // Transient resource preparation: TUN traffic is blocked until complete rules are available.
    public bool ResourceBootstrap { get; set; }
    public string? ResourceNodeId { get; set; }
    // Startup may close the resource route while automatic groups await observations after TUN creation.
    public bool DeferAutomaticGroupSelection { get; set; }
    public List<InterfaceBinding> Interfaces { get; set; } = [];
    public List<GroupProbe> Probes { get; set; } = [];
    public List<RuleSourceBinding> PreparedRuleSources { get; set; } = [];
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public TimeSpan ProbeMaxAge { get; set; } = TimeSpan.FromMinutes(10);
    public string ChinaGeoSiteFile { get; set; } = "geosite.dat";
    public string ChinaGeoIpFile { get; set; } = "geoip.dat";
    public string ChinaTag { get; set; } = "cn";
}
public sealed record XrayCompileResult(string? Json, IReadOnlyList<PolicyDiagnostic> Diagnostics)
{
    public bool IsSuccess => Json is not null && !Diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error);
}
