using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EgressController.Policy;

public static partial class XrayConfigCompiler
{
    public const string MinimumCoreVersion = "26.10.29";
    public static IReadOnlyList<string> LocalNetworkCidrs { get; } = Array.AsReadOnly(new[]
    {
        "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12", "192.168.0.0/16",
        "198.18.0.0/15", "224.0.0.0/4", "255.255.255.255/32", "::1/128", "fc00::/7", "fe80::/10", "ff00::/8"
    });
    public static XrayCompileResult Compile(MacPolicyDocument doc, XrayCompileOptions options)
    {
        var diagnostics = PolicyValidator.Validate(doc).ToList();
        var home = options.Interfaces.FirstOrDefault(x => x.StableId == doc.Interfaces.HomeId)?.BsdName;
        var nonhome = options.Interfaces.FirstOrDefault(x => x.StableId == doc.Interfaces.NonHomeId)?.BsdName;
        if (home is null || nonhome is null || home == nonhome || !BsdName().IsMatch(home) || !BsdName().IsMatch(nonhome) || home.StartsWith("utun", StringComparison.Ordinal) || nonhome.StartsWith("utun", StringComparison.Ordinal) || home == "lo0" || nonhome == "lo0" || options.Interfaces.Select(x => x.StableId).Distinct(StringComparer.Ordinal).Count() != options.Interfaces.Count) diagnostics.Add(new("interfaces.unavailable", "保存的两张物理网卡未解析到不同的当前 BSD 接口；拒绝自动换卡。"));
        if (!SafeDatFile(options.ChinaGeoSiteFile) || !SafeDatFile(options.ChinaGeoIpFile) || !SafeDataTag(options.ChinaTag)) diagnostics.Add(new("rules.china", "内置中国规则文件或标签无效。"));
        if (diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error)) return new(null, diagnostics);
        var selectedNodes = new Dictionary<string, ProxyNode>(StringComparer.Ordinal);
        var selections = new Dictionary<string, string>(StringComparer.Ordinal);
        string? Resolve(string id)
        {
            if (selections.TryGetValue(id, out var found)) return found;
            var selected = GroupSelector.Resolve(id, doc, options.Probes, options.Now, options.ProbeMaxAge);
            if (options.ResourceBootstrap && options.DeferAutomaticGroupSelection && !selected.IsSuccess
                && selected.Diagnostics.Count > 0 && selected.Diagnostics.All(item => item.Code == "group.probes"))
                return "blocked"; // No first-node substitute, direct fallback, or synthetic observation.
            diagnostics.AddRange(selected.Diagnostics);
            if (selected.NodeId is not { } nodeId) return null;
            if (nodeId == GroupGraph.Direct) return "nonhome";
            if (nodeId == GroupGraph.Reject) return "blocked";
            selections[id] = nodeId;
            var node = doc.Nodes.First(x => x.Id == nodeId); selectedNodes[nodeId] = node;
            if (node.DialerProxyId is { } dialer) _ = Resolve(dialer);
            return nodeId;
        }
        string? Outbound(ExitChoice via) => via.Kind switch { ExitKind.Home => "home", ExitKind.NonHome => "nonhome", _ => Resolve(via.TargetId ?? "") };
        var global = Outbound(doc.Defaults.Global);
        string? resource = options.ResourceNodeId is { } resourceId && GroupGraph.Leaves(doc.Defaults.Global.TargetId ?? "", doc).Contains(resourceId)
            && doc.Nodes.Any(node => node.Id == resourceId && node.IsSupported) ? Resolve(resourceId) : global;
        if (resource is "nonhome" or "blocked")
        {
            resource = GroupGraph.ResourceNode(doc, options.Probes, options.Now) is { } available ? Resolve(available) : "blocked";
        }
        // Load valid subscription nodes once, so group changes only replace the routing table.
        // Unselected unsupported items stay visible without blocking otherwise valid routes.
        foreach (var candidate in doc.Nodes.Where(node => node.IsSupported))
        {
            var selection = GroupSelector.Resolve(candidate.Id, doc, options.Probes, options.Now, options.ProbeMaxAge);
            if (selection.IsSuccess) _ = Resolve(candidate.Id);
        }
        foreach (var app in doc.Applications.Where(x => x.Enabled && !options.ResourceBootstrap)) _ = Outbound(app.Via);
        foreach (var rule in doc.RuleSets.Where(x => x.Enabled && !options.ResourceBootstrap)) _ = Outbound(rule.Via);
        var rules = new JsonArray
        {
            Rule("resources", resource, ("inboundTag", Strings(["resources"]))),
            Rule("dns-upstream", "nonhome", ("inboundTag", Strings(["dns-query"])))
        };
        if (!options.ResourceBootstrap)
        {
            rules.Add(Rule("system-dns", "dns", ("port", JsonValue.Create(53))));
            rules.Add(Rule("local-network", "nonhome", ("ip", Strings(LocalNetworkCidrs))));
        }
        foreach (var app in doc.Applications.Where(x => x.Enabled && !options.ResourceBootstrap)) rules.Add(Rule("app-" + app.Id, Outbound(app.Via), ("process", Strings(app.ExecutablePaths.Distinct(StringComparer.Ordinal)))));
        foreach (var rule in doc.RuleSets.Where(x => x.Enabled && !options.ResourceBootstrap))
        {
            var domains = rule.Domains.Select(x => x.Kind switch { DomainMatchKind.Full => "full:" + DomainRuleValidation.CompiledValue(x), DomainMatchKind.Suffix => "domain:" + DomainRuleValidation.CompiledValue(x), DomainMatchKind.Keyword => "keyword:" + x.Value, _ => "regexp:" + x.Value }).ToList();
            var ips = rule.IpCidrs.ToList();
            foreach (var source in rule.Sources)
            {
                var binding = options.PreparedRuleSources.FirstOrDefault(x => x.Source == source);
                if (binding is null || !SafeDataTag(binding.Tag) || binding.DomainFile is null && binding.IpFile is null || binding.DomainFile is { } d && !SafeDatFile(d) || binding.IpFile is { } i && !SafeDatFile(i))
                { diagnostics.Add(new("rules.not-prepared", "规则来源未完成验证和 DAT 编译；保留原配置。", ObjectId: rule.Id)); continue; }
                if (binding.DomainFile is { } domainFile) domains.Add("ext:" + domainFile + ":" + binding.Tag);
                if (binding.IpFile is { } ipFile) ips.Add("ext:" + ipFile + ":" + binding.Tag);
            }
            // Two independent rules implement domain OR IP, never accidental AND.
            if (domains.Count > 0) rules.Add(Rule("domains-" + rule.Id, Outbound(rule.Via), ("domain", Strings(domains))));
            if (ips.Count > 0) rules.Add(Rule("ips-" + rule.Id, Outbound(rule.Via), ("ip", Strings(ips))));
            if (domains.Count == 0 && ips.Count == 0) diagnostics.Add(new("rules.empty", "启用的规则集合没有可执行规则。", ObjectId: rule.Id));
        }
        if (!options.ResourceBootstrap)
        {
            rules.Add(Rule("china-domains", "nonhome", ("domain", Strings(["ext:" + options.ChinaGeoSiteFile + ":" + options.ChinaTag]))));
            rules.Add(Rule("china-ips", "nonhome", ("ip", Strings(["ext:" + options.ChinaGeoIpFile + ":" + options.ChinaTag]))));
        }
        rules.Add(Rule(options.ResourceBootstrap ? "bootstrap-block" : "global", options.ResourceBootstrap ? "blocked" : global, ("network", JsonValue.Create("tcp,udp"))));
        if (diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error)) return new(null, diagnostics.Distinct().ToList());
        var outbounds = new JsonArray
        {
            (JsonNode)new JsonObject { ["tag"] = "blocked", ["protocol"] = "blackhole", ["settings"] = new JsonObject() },
            Direct("home", home!), Direct("nonhome", nonhome!),
            (JsonNode)new JsonObject { ["tag"] = "dns", ["protocol"] = "dns", ["settings"] = new JsonObject { ["rules"] = new JsonArray((JsonNode)new JsonObject { ["action"] = "hijack", ["qType"] = "1,28" }, (JsonNode)new JsonObject { ["action"] = "return", ["rCode"] = 0 }) }, ["streamSettings"] = Stream(nonhome!) }
        };
        foreach (var node in selectedNodes.Values.OrderBy(x => x.Id, StringComparer.Ordinal)) outbounds.Add(NodeOutbound(node, nonhome!, selections));
        var config = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "info" },
            ["dns"] = new JsonObject { ["tag"] = "dns-query", ["queryStrategy"] = "UseIP", ["disableFallback"] = true, ["servers"] = Strings([doc.Dns.HttpsUrl]) },
            ["inbounds"] = new JsonArray(new JsonObject { ["tag"] = "tun", ["protocol"] = "tun", ["settings"] = new JsonObject { ["name"] = doc.Tun.Name, ["mtu"] = doc.Tun.Mtu, ["gateway"] = Strings([doc.Tun.Ipv4Cidr]), ["autoSystemRoutingTable"] = Strings(["0.0.0.0/0", "::/0"]), ["autoOutboundsInterface"] = "" }, ["sniffing"] = new JsonObject { ["enabled"] = true, ["destOverride"] = Strings(["http", "tls", "quic"]), ["routeOnly"] = false } }),
            // IPOnDemand is required: an explicit final catch-all defeats IPIfNonMatch's second pass.
            ["routing"] = new JsonObject { ["domainStrategy"] = "IPOnDemand", ["rules"] = rules },
            ["outbounds"] = outbounds
        };
        config["inbounds"]!.AsArray().Add((JsonNode)new JsonObject
        {
            ["tag"] = "resources", ["listen"] = "127.0.0.1", ["port"] = doc.Defaults.ResourceProxyPort,
            ["protocol"] = "socks", ["settings"] = new JsonObject { ["auth"] = "noauth", ["udp"] = false }
        });
        return new(config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), diagnostics.Distinct().ToList());
    }

    private static JsonNode NodeOutbound(ProxyNode node, string iface, Dictionary<string, string> selections)
    {
        var settings = new JsonObject { ["address"] = node.Server, ["port"] = node.Port };
        var protocol = node.Protocol switch { NodeProtocol.Vless => "vless", NodeProtocol.Vmess => "vmess", NodeProtocol.Shadowsocks => "shadowsocks", NodeProtocol.Trojan => "trojan", NodeProtocol.Socks => "socks", _ => "http" };
        switch (node.Protocol)
        {
            case NodeProtocol.Vless: settings["id"] = node.UserId; settings["encryption"] = node.Encryption; if (node.Flow.Length > 0) settings["flow"] = node.Flow; break;
            case NodeProtocol.Vmess: settings["id"] = node.UserId; settings["security"] = node.Security; break;
            case NodeProtocol.Shadowsocks: settings["method"] = node.Cipher; settings["password"] = node.Password; break;
            case NodeProtocol.Trojan: settings["password"] = node.Password; break;
            case NodeProtocol.Socks:
            case NodeProtocol.Http: if (node.Username.Length > 0) { settings["user"] = node.Username; settings["pass"] = node.Password; } break;
        }
        var stream = Stream(iface);
        stream["network"] = node.Network == "tcp" ? "raw" : node.Network;
        if (node.TcpFastOpen is { } tfo) stream["sockopt"]!["tcpFastOpen"] = tfo;
        if (node.Plugin == "obfs" && node.PluginMode == "http")
        {
            // simple-obfs validates HTTP/1.1 + Upgrade + optional Host, strips the first header,
            // and then relays the unchanged Shadowsocks stream. This is not WebSocket framing.
            stream["rawSettings"] = new JsonObject { ["header"] = new JsonObject { ["type"] = "http",
                ["request"] = new JsonObject { ["version"] = "1.1", ["method"] = "GET", ["path"] = Strings(["/"]),
                    ["headers"] = new JsonObject { ["Host"] = Strings([node.PluginHost + (node.Port == 80 ? "" : ":" + node.Port.ToString(System.Globalization.CultureInfo.InvariantCulture))]),
                        ["Upgrade"] = Strings(["websocket"]), ["Connection"] = Strings(["Upgrade"]), ["User-Agent"] = Strings(["curl/7.88.0"]) } } } };
        }
        if (node.DialerProxyId is { } dialer) stream["sockopt"]!["dialerProxy"] = selections[dialer];
        if (node.RealityPublicKey.Length > 0)
        {
            stream["security"] = "reality";
            stream["realitySettings"] = new JsonObject { ["serverName"] = node.ServerName, ["fingerprint"] = node.Fingerprint.Length > 0 ? node.Fingerprint : "chrome", ["password"] = node.RealityPublicKey, ["shortId"] = node.RealityShortId, ["spiderX"] = node.RealitySpiderX };
        }
        else if (node.Tls)
        {
            stream["security"] = "tls";
            var tls = new JsonObject { ["serverName"] = node.ServerName.Length > 0 ? node.ServerName : node.Server };
            if (node.Alpn.Count > 0) tls["alpn"] = Strings(node.Alpn);
            if (node.Fingerprint.Length > 0) tls["fingerprint"] = node.Fingerprint;
            stream["tlsSettings"] = tls;
        }
        if (node.Network == "ws") stream["wsSettings"] = new JsonObject { ["path"] = node.TransportPath, ["host"] = node.TransportHost };
        if (node.Network == "grpc") stream["grpcSettings"] = new JsonObject { ["serviceName"] = node.GrpcServiceName };
        if (node.Network == "httpupgrade") stream["httpupgradeSettings"] = new JsonObject { ["path"] = node.TransportPath, ["host"] = node.TransportHost };
        return new JsonObject() { ["tag"] = node.Id, ["protocol"] = protocol, ["settings"] = settings, ["streamSettings"] = stream };
    }
    private static JsonObject Stream(string iface) => new() { ["sockopt"] = new JsonObject { ["interface"] = iface, ["domainStrategy"] = "ForceIP", ["happyEyeballs"] = new JsonObject { ["prioritizeIPv6"] = false, ["tryDelayMs"] = 250, ["interleave"] = 1, ["maxConcurrentTry"] = 2 } } };
    private static JsonNode Direct(string tag, string iface) => new JsonObject() { ["tag"] = tag, ["protocol"] = "freedom", ["settings"] = new JsonObject(), ["streamSettings"] = Stream(iface) };
    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
    private static JsonNode Rule(string name, string? outbound, params (string Key, JsonNode? Value)[] values)
    {
        var rule = new JsonObject { ["ruleTag"] = name, ["outboundTag"] = outbound };
        foreach (var (key, value) in values) rule[key] = value;
        return rule;
    }
    private static bool SafeDatFile(string value) => value.EndsWith(".dat", StringComparison.Ordinal) && !value.Contains('/') && !value.Contains('\\') && !value.Contains(':') && value.Length is > 4 and < 128 && SafeTag().IsMatch(value);
    private static bool SafeDataTag(string value) => value.Length <= 127 && SafeTag().IsMatch(value);
    [GeneratedRegex("^[a-z][a-z0-9]{0,15}$", RegexOptions.CultureInvariant)] private static partial Regex BsdName();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$", RegexOptions.CultureInvariant)] private static partial Regex SafeTag();
}
