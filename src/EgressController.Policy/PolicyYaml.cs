using YamlDotNet.RepresentationModel;

namespace EgressController.Policy;

/// <summary>Explicit YAML tree mapping: no reflection serializer or runtime type construction.</summary>
public static class PolicyYaml
{
    public static PolicyReadResult Read(string yaml)
    {
        try
        {
            var root = YamlNodes.Load(yaml);
            var shapeErrors = DslShapeValidator.Validate(root);
            if (shapeErrors.Count > 0) return new(null, shapeErrors);
            var doc = new MacPolicyDocument { Version = root.Integer("version") };
            var interfaces = root.Map("interfaces");
            doc.Interfaces = new() { HomeId = interfaces.Text("home"), NonHomeId = interfaces.Text("nonhome") };
            var defaults = root.Map("defaults");
            doc.Defaults = new() { China = ReadExit(defaults.Get("china")), Global = ReadExit(defaults.Get("global")), ProxyInterface = defaults.Text("proxy_interface", "nonhome"), ResourceProxyPort = defaults.Integer("resource_proxy_port", 17890) };
            var dns = root.Map("dns"); doc.Dns = new() { UpstreamIp = dns.Text("upstream_ip", "1.1.1.1"), HttpsUrl = dns.Text("https_url", "https://1.1.1.1/dns-query") };
            var tun = root.Map("tun"); doc.Tun = new() { Name = tun.Text("name", "utun10"), Ipv4Cidr = tun.Text("ipv4_cidr", "198.18.0.1/30"), Mtu = tun.Integer("mtu", 1500) };
            foreach (var app in root.Items("applications").OfType<YamlMappingNode>())
                doc.Applications.Add(new() { Id = app.Text("id"), Name = app.Text("name"), BundlePath = app.Text("path"), BundleIdentifier = Null(app.Text("bundle_id")), PrimaryExecutable = Null(app.Text("primary_executable")), ExecutablePaths = app.Strings("executables"), Via = ReadExit(app.Get("via")), Enabled = app.Boolean("enabled", true) });
            foreach (var rule in root.Items("rule_sets").OfType<YamlMappingNode>())
                doc.RuleSets.Add(new() { Id = rule.Text("id"), Name = rule.Text("name"), Sources = rule.Strings("sources"), Domains = rule.Items("domains").OfType<YamlMappingNode>().Select(x => new DomainRule(ParseDomainKind(x.Text("kind")), x.Text("value"))).ToList(), IpCidrs = rule.Strings("ip_cidrs"), Via = ReadExit(rule.Get("via")), Enabled = rule.Boolean("enabled", true) });
            foreach (var node in root.Items("nodes").OfType<YamlMappingNode>()) doc.Nodes.Add(ReadNode(node));
            foreach (var group in root.Items("groups").OfType<YamlMappingNode>())
                doc.Groups.Add(new() { Id = group.Text("id"), Name = group.Text("name"), SourceId = group.Text("source_id"), Kind = ParseGroupKind(group.Text("kind")), Retained = group.Boolean("retained", true), StrategyOverridden = group.Boolean("strategy_override"), Icon = group.Text("icon"), MemberIds = group.Strings("members"), ProviderIds = group.Strings("providers"), SelectedMemberId = Null(group.Text("selected")), TestUrl = group.Text("test_url", "https://www.gstatic.com/generate_204"), IntervalSeconds = group.Integer("interval", 300), ToleranceMilliseconds = group.Integer("tolerance", 50), Diagnostics = ReadDiagnostics(group) });
            foreach (var source in root.Items("subscriptions").OfType<YamlMappingNode>())
                doc.Subscriptions.Add(new() { Id = source.Text("id"), Name = source.Text("name"), Url = source.Text("url") });
            foreach (var provider in root.Items("providers").OfType<YamlMappingNode>())
                doc.Providers.Add(new() { Id = provider.Text("id"), Name = provider.Text("name"), SourceId = provider.Text("source_id"), Url = provider.Text("url"), Diagnostics = ReadDiagnostics(provider) });
            var errors = PolicyValidator.Validate(doc).ToList();
            var allowed = new HashSet<string>(["version", "interfaces", "defaults", "dns", "tun", "applications", "rule_sets", "nodes", "groups", "providers", "subscriptions"], StringComparer.Ordinal);
            if (root.Children.Keys.OfType<YamlScalarNode>().Any(x => !allowed.Contains(x.Value ?? ""))) errors.Add(new("dsl.unknown-field", "配置包含无法识别的顶层字段。"));
            return new(doc, errors);
        }
        catch (Exception e) when (e is YamlDotNet.Core.YamlException or FormatException or ArgumentException or InvalidOperationException)
        {
            // Parser exceptions may contain credential-bearing input excerpts.
            return new(null, [new("dsl.syntax", "YAML 结构无效、存在递归别名或超出安全限制；未应用配置。")]);
        }
    }

    public static string Write(MacPolicyDocument doc)
    {
        var root = new YamlMappingNode
        {
            { "version", doc.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            { "interfaces", new YamlMappingNode { { "home", doc.Interfaces.HomeId }, { "nonhome", doc.Interfaces.NonHomeId } } },
            { "defaults", new YamlMappingNode { { "china", WriteExit(doc.Defaults.China) }, { "global", WriteExit(doc.Defaults.Global) }, { "proxy_interface", doc.Defaults.ProxyInterface }, { "resource_proxy_port", doc.Defaults.ResourceProxyPort.ToString(System.Globalization.CultureInfo.InvariantCulture) } } },
            { "dns", new YamlMappingNode { { "upstream_ip", doc.Dns.UpstreamIp }, { "https_url", doc.Dns.HttpsUrl } } },
            { "tun", new YamlMappingNode { { "name", doc.Tun.Name }, { "ipv4_cidr", doc.Tun.Ipv4Cidr }, { "mtu", doc.Tun.Mtu.ToString(System.Globalization.CultureInfo.InvariantCulture) } } },
            { "applications", new YamlSequenceNode(doc.Applications.Select(app => new YamlMappingNode { { "id", app.Id }, { "name", app.Name }, { "path", app.BundlePath }, { "bundle_id", app.BundleIdentifier ?? "" }, { "primary_executable", app.PrimaryExecutable ?? "" }, { "executables", YamlNodes.Sequence(app.ExecutablePaths) }, { "via", WriteExit(app.Via) }, { "enabled", app.Enabled ? "true" : "false" } })) },
            { "rule_sets", new YamlSequenceNode(doc.RuleSets.Select(rule => new YamlMappingNode { { "id", rule.Id }, { "name", rule.Name }, { "sources", YamlNodes.Sequence(rule.Sources) }, { "domains", new YamlSequenceNode(rule.Domains.Select(x => new YamlMappingNode { { "kind", x.Kind.ToString().ToLowerInvariant() }, { "value", x.Value } })) }, { "ip_cidrs", YamlNodes.Sequence(rule.IpCidrs) }, { "via", WriteExit(rule.Via) }, { "enabled", rule.Enabled ? "true" : "false" } })) },
            { "nodes", new YamlSequenceNode(doc.Nodes.Select(WriteNode)) },
            { "groups", new YamlSequenceNode(doc.Groups.Select(group => new YamlMappingNode { { "id", group.Id }, { "name", group.Name }, { "source_id", group.SourceId }, { "kind", GroupText(group.Kind) }, { "retained", group.Retained ? "true" : "false" }, { "strategy_override", group.StrategyOverridden ? "true" : "false" }, { "icon", group.Icon }, { "members", YamlNodes.Sequence(group.MemberIds) }, { "providers", YamlNodes.Sequence(group.ProviderIds) }, { "selected", group.SelectedMemberId ?? "" }, { "test_url", group.TestUrl }, { "interval", group.IntervalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) }, { "tolerance", group.ToleranceMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) }, { "diagnostics", WriteDiagnostics(group.Diagnostics) } })) },
            { "subscriptions", new YamlSequenceNode(doc.Subscriptions.Select(source => new YamlMappingNode { { "id", source.Id }, { "name", source.Name }, { "url", source.Url } })) },
            { "providers", new YamlSequenceNode(doc.Providers.Select(provider => new YamlMappingNode { { "id", provider.Id }, { "name", provider.Name }, { "source_id", provider.SourceId }, { "url", provider.Url }, { "diagnostics", WriteDiagnostics(provider.Diagnostics) } })) }
        };
        return YamlNodes.Save(root);
    }

    internal static ProxyNode ReadNode(YamlMappingNode n) => new()
    {
        Id = n.Text("id"),
        Name = n.Text("name"),
        SourceId = n.Text("source_id"),
        Protocol = ParseProtocol(n.Text("protocol")),
        Server = n.Text("server"),
        Port = n.Integer("port"),
        UserId = n.Text("user_id"),
        Password = n.Text("password"),
        Username = n.Text("username"),
        Cipher = n.Text("cipher"),
        Encryption = n.Text("encryption", "none"),
        Flow = n.Text("flow"),
        Security = n.Text("security", "auto"),
        Network = n.Text("network", "tcp"),
        Tls = n.Boolean("tls"),
        SkipCertificateVerify = n.Boolean("skip_certificate_verify"),
        ServerName = n.Text("server_name"),
        Fingerprint = n.Text("fingerprint"),
        RealityPublicKey = n.Text("reality_public_key"),
        RealityShortId = n.Text("reality_short_id"),
        RealitySpiderX = n.Text("reality_spider_x"),
        Alpn = n.Strings("alpn"),
        TransportPath = n.Text("transport_path"),
        TransportHost = n.Text("transport_host"),
        GrpcServiceName = n.Text("grpc_service_name"),
        Plugin = n.Text("plugin"), PluginMode = n.Text("plugin_mode"), PluginHost = n.Text("plugin_host"),
        TcpFastOpen = n.Get("tcp_fast_open") is null ? null : n.Boolean("tcp_fast_open"),
        DialerProxyId = Null(n.Text("dialer_proxy")),
        Diagnostics = ReadDiagnostics(n)
    };
    private static YamlMappingNode WriteNode(ProxyNode n)
    {
        var result = new YamlMappingNode
        {
        { "id", n.Id }, { "name", n.Name }, { "source_id", n.SourceId }, { "protocol", n.Protocol.ToString().ToLowerInvariant() }, { "server", n.Server }, { "port", n.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) }, { "user_id", n.UserId }, { "password", n.Password }, { "username", n.Username }, { "cipher", n.Cipher }, { "encryption", n.Encryption }, { "flow", n.Flow }, { "security", n.Security }, { "network", n.Network }, { "tls", n.Tls ? "true" : "false" }, { "skip_certificate_verify", n.SkipCertificateVerify ? "true" : "false" }, { "server_name", n.ServerName }, { "fingerprint", n.Fingerprint }, { "reality_public_key", n.RealityPublicKey }, { "reality_short_id", n.RealityShortId }, { "reality_spider_x", n.RealitySpiderX }, { "alpn", YamlNodes.Sequence(n.Alpn) }, { "transport_path", n.TransportPath }, { "transport_host", n.TransportHost }, { "grpc_service_name", n.GrpcServiceName }, { "dialer_proxy", n.DialerProxyId ?? "" }, { "diagnostics", WriteDiagnostics(n.Diagnostics) }
        };
        if (n.Plugin.Length > 0 || n.PluginMode.Length > 0 || n.PluginHost.Length > 0)
        { result.Add("plugin", n.Plugin); result.Add("plugin_mode", n.PluginMode); result.Add("plugin_host", n.PluginHost); }
        if (n.TcpFastOpen is { } tfo) result.Add("tcp_fast_open", tfo ? "true" : "false");
        return result;
    }
    private static List<PolicyDiagnostic> ReadDiagnostics(YamlMappingNode n) => n.Items("diagnostics").OfType<YamlMappingNode>().Select(x => new PolicyDiagnostic(x.Text("code"), x.Text("message"), x.Text("severity") == "warning" ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error)).ToList();
    private static YamlSequenceNode WriteDiagnostics(IEnumerable<PolicyDiagnostic> values) => new(values.Select(x => new YamlMappingNode { { "code", x.Code }, { "message", x.Message }, { "severity", x.Severity == DiagnosticSeverity.Warning ? "warning" : "error" } }));
    private static string? Null(string value) => string.IsNullOrEmpty(value) ? null : value;
    private static ExitChoice ReadExit(YamlNode? node) => node switch
    {
        YamlScalarNode { Value: "home" } => ExitChoice.Home,
        YamlScalarNode { Value: "nonhome" } => ExitChoice.NonHome,
        YamlMappingNode map when map.Get("proxy") is not null => ExitChoice.Proxy(map.Text("proxy")),
        YamlMappingNode map when map.Get("proxy_group") is not null => ExitChoice.Proxy(map.Text("proxy_group")),
        _ => ExitChoice.Proxy("")
    };
    private static YamlNode WriteExit(ExitChoice exit) => exit.Kind switch { ExitKind.Home => new YamlScalarNode("home"), ExitKind.NonHome => new YamlScalarNode("nonhome"), _ => new YamlMappingNode { { "proxy", exit.TargetId ?? "" } } };
    private static DomainMatchKind ParseDomainKind(string value) => value switch { "full" => DomainMatchKind.Full, "suffix" => DomainMatchKind.Suffix, "keyword" => DomainMatchKind.Keyword, "regex" => DomainMatchKind.Regex, _ => throw new FormatException("Unknown domain kind.") };
    internal static GroupKind ParseGroupKind(string value) => value switch { "select" => GroupKind.Select, "url-test" or "urltest" => GroupKind.UrlTest, "fallback" => GroupKind.Fallback, _ => GroupKind.Unsupported };
    internal static string GroupText(GroupKind value) => value switch { GroupKind.Select => "select", GroupKind.UrlTest => "url-test", GroupKind.Fallback => "fallback", _ => "unsupported" };
    internal static NodeProtocol ParseProtocol(string value) => value.ToLowerInvariant() switch { "vless" => NodeProtocol.Vless, "vmess" => NodeProtocol.Vmess, "ss" or "shadowsocks" => NodeProtocol.Shadowsocks, "trojan" => NodeProtocol.Trojan, "socks" or "socks5" => NodeProtocol.Socks, "http" => NodeProtocol.Http, _ => NodeProtocol.Unsupported };
}
