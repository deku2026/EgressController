using System.Security.Cryptography;
using System.Text;
using YamlDotNet.RepresentationModel;

namespace EgressController.Policy;

public static class ClashSubscriptionImporter
{
    /// <summary>Parses provided text only. Caller fetches the URL and optional provider bodies.</summary>
    public static ClashImportResult Import(string yaml, string sourceId, IReadOnlyDictionary<string, string>? providerPayloads = null)
    {
        var nodes = new List<ProxyNode>(); var groups = new List<ProxyGroup>(); var providers = new List<ProxyProvider>(); var diagnostics = new List<PolicyDiagnostic>();
        try
        {
            var root = YamlNodes.Load(yaml);
            if (string.IsNullOrWhiteSpace(sourceId)) return new([], [], [], [new("subscription.id", "订阅必须具有稳定标识。")]);
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            void AddNode(YamlMappingNode value, string scope)
            {
                var node = ReadNode(value, sourceId, scope);
                if (names.ContainsKey(node.Name)) { diagnostics.Add(new("subscription.duplicate-name", "订阅中存在重复节点或分组名称。", ObjectId: node.Id)); return; }
                names[node.Name] = node.Id;
                if (nodes.Any(n => n.Id == node.Id)) node.Id = StableId("node", sourceId, node.Id + "\n" + node.Name);
                names[node.Name] = node.Id; nodes.Add(node);
            }
            foreach (var item in root.Items("proxies").OfType<YamlMappingNode>()) AddNode(item, sourceId);
            var providerMembers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var providerMap = root.Map("proxy-providers");
            foreach (var entry in providerMap.Children)
            {
                if (entry.Key is not YamlScalarNode providerName || entry.Value is not YamlMappingNode value) continue;
                var name = providerName.Value ?? ""; var provider = new ProxyProvider { Id = StableId("provider", sourceId, name), Name = name, SourceId = sourceId, Url = value.Text("url") };
                if (value.Text("type") != "http" || !YamlNodes.IsWebUrl(provider.Url)) provider.Diagnostics.Add(new("provider.unsupported", "仅支持 HTTP(S) URL 节点提供器；未读取本地文件或执行命令。", ObjectId: provider.Id));
                providers.Add(provider);
                if (providerPayloads is not null && (providerPayloads.TryGetValue(provider.Id, out var body) || providerPayloads.TryGetValue(name, out body)))
                {
                    var providerRoot = YamlNodes.Load(body);
                    var start = nodes.Count;
                    foreach (var item in providerRoot.Items("proxies").OfType<YamlMappingNode>()) AddNode(item, provider.Id);
                    providerMembers[name] = nodes.Skip(start).Select(x => x.Id).ToList();
                }
                else provider.Diagnostics.Add(new("provider.pending", "节点提供器尚未获取；引用它的分组不可应用。", ObjectId: provider.Id));
            }
            var groupMaps = root.Items("proxy-groups").OfType<YamlMappingNode>().ToList();
            foreach (var value in groupMaps)
            {
                var name = value.Text("name"); var id = StableId("group", sourceId, name);
                if (!names.TryAdd(name, id)) { diagnostics.Add(new("subscription.duplicate-name", "订阅中存在重复节点或分组名称。", ObjectId: id)); continue; }
                groups.Add(new() { Id = id, Name = name, SourceId = sourceId, Kind = PolicyYaml.ParseGroupKind(value.Text("type")), TestUrl = value.Text("url", "https://www.gstatic.com/generate_204"), IntervalSeconds = value.Integer("interval", 300), ToleranceMilliseconds = value.Integer("tolerance", 50) });
            }
            foreach (var group in groups)
            {
                var value = groupMaps.First(x => x.Text("name") == group.Name);
                if (group.Kind == GroupKind.Unsupported) group.Diagnostics.Add(new("group.unsupported", "该 Clash 分组类型无法等价执行。", ObjectId: group.Id));
                foreach (var member in value.Strings("proxies"))
                {
                    if (member == "DIRECT") group.MemberIds.Add("builtin:direct");
                    else if (member == "REJECT" || member == "REJECT-DROP") group.MemberIds.Add("builtin:reject");
                    else if (names.TryGetValue(member, out var memberId)) group.MemberIds.Add(memberId);
                    else group.Diagnostics.Add(new("group.missing-member", "分组引用的节点或分组不存在。", ObjectId: group.Id));
                }
                foreach (var providerName in value.Strings("use"))
                {
                    var provider = providers.FirstOrDefault(x => x.Name == providerName);
                    if (provider is null) group.Diagnostics.Add(new("group.missing-provider", "分组引用的节点提供器不存在。", ObjectId: group.Id));
                    else { group.ProviderIds.Add(provider.Id); group.Diagnostics.AddRange(provider.Diagnostics); if (providerMembers.TryGetValue(providerName, out var members)) group.MemberIds.AddRange(members); }
                }
                // Never infer the first manual selection. The UI must choose an actual member.
                group.SelectedMemberId = null;
                if (value.Get("filter") is not null || value.Get("exclude-filter") is not null || value.Boolean("include-all") || value.Boolean("include-all-proxies") || value.Boolean("include-all-providers"))
                    group.Diagnostics.Add(new("group.dynamic-membership", "动态分组筛选暂不支持，未静默扩大或缩小成员。", ObjectId: group.Id));
                var allowedGroupKeys = new HashSet<string>(["<<", "name", "type", "proxies", "use", "url", "interval", "tolerance", "icon", "hidden", "filter", "exclude-filter", "include-all", "include-all-proxies", "include-all-providers"], StringComparer.Ordinal);
                if (value.Keys().Any(key => !allowedGroupKeys.Contains(key))) group.Diagnostics.Add(new("group.unknown-option", "分组包含尚未实现等价语义的选项。", ObjectId: group.Id));
            }
            foreach (var node in nodes)
            {
                if (node.DialerProxyId is { } name)
                {
                    if (names.TryGetValue(name, out var id)) node.DialerProxyId = id;
                    else node.Diagnostics.Add(new("node.missing-dialer", "节点的前置代理不存在。", ObjectId: node.Id));
                }
            }
            var ignored = new[] { "dns", "tun", "rules", "rule-providers", "mixed-port", "port", "socks-port", "redir-port", "tproxy-port", "external-controller", "allow-lan", "mode" };
            foreach (var key in ignored.Where(key => root.Get(key) is not null)) diagnostics.Add(new("subscription.ignored." + key, "订阅中的 " + key + " 不属于节点来源，已忽略。", DiagnosticSeverity.Warning));
            diagnostics.AddRange(nodes.SelectMany(x => x.Diagnostics)); diagnostics.AddRange(groups.SelectMany(x => x.Diagnostics)); diagnostics.AddRange(providers.SelectMany(x => x.Diagnostics));
            diagnostics.AddRange(PolicyValidator.ValidateGraph(nodes, groups));
            if (nodes.Count == 0 && providers.Count == 0) diagnostics.Add(new("subscription.empty", "订阅未提供任何可导入节点。"));
            return new(nodes, groups, providers, diagnostics.Distinct().ToList());
        }
        catch (Exception e) when (e is YamlDotNet.Core.YamlException or FormatException or ArgumentException or InvalidOperationException)
        { return new([], [], [], [new("subscription.syntax", "订阅 YAML 结构无效或超出安全限制；未导入。")]); }
    }

    private static ProxyNode ReadNode(YamlMappingNode value, string sourceId, string scope)
    {
        var reality = value.Map("reality-opts"); var ws = value.Map("ws-opts"); var grpc = value.Map("grpc-opts"); var httpUpgrade = value.Map("http-upgrade-opts");
        var node = new ProxyNode
        {
            Name = value.Text("name"),
            SourceId = sourceId,
            Protocol = PolicyYaml.ParseProtocol(value.Text("type")),
            Server = value.Text("server"),
            Port = value.Integer("port"),
            UserId = value.Text("uuid"),
            Password = value.Text("password"),
            Username = value.Text("username"),
            Cipher = value.Text("cipher"),
            Encryption = value.Text("encryption", "none"),
            Flow = value.Text("flow"),
            Security = value.Text("cipher", "auto"),
            Network = value.Text("network", "tcp"),
            Tls = value.Boolean("tls") || value.Text("type") == "trojan" || reality.Get("public-key") is not null,
            SkipCertificateVerify = value.Boolean("skip-cert-verify"),
            ServerName = value.Text("servername", value.Text("sni")),
            Fingerprint = value.Text("client-fingerprint"),
            RealityPublicKey = reality.Text("public-key"),
            RealityShortId = reality.Text("short-id"),
            RealitySpiderX = reality.Text("spider-x"),
            Alpn = value.Strings("alpn"),
            DialerProxyId = string.IsNullOrEmpty(value.Text("dialer-proxy")) ? null : value.Text("dialer-proxy"),
            TransportPath = ws.Text("path", httpUpgrade.Text("path")),
            TransportHost = ws.Map("headers").Text("Host", ws.Map("headers").Text("host", httpUpgrade.Text("host"))),
            GrpcServiceName = grpc.Text("grpc-service-name")
        };
        node.Id = StableId("node", scope, string.Join('\n', value.Text("type"), node.Server.ToLowerInvariant(), node.Port, node.UserId, node.Username, node.Network, node.ServerName, node.Flow));
        if (node.Protocol == NodeProtocol.Unsupported) node.Diagnostics.Add(new("node.unsupported-protocol", "节点协议不受当前 Xray 编译器支持。", ObjectId: node.Id));
        if (value.Integer("alterId", value.Integer("alter-id")) != 0) node.Diagnostics.Add(new("node.legacy-vmess", "不支持带 alterId 的旧 VMess。", ObjectId: node.Id));
        if (value.Get("plugin") is not null || value.Get("plugin-opts") is not null) node.Diagnostics.Add(new("node.plugin", "Shadowsocks 插件无法等价转换。", ObjectId: node.Id));
        if (node.Network is not ("tcp" or "ws" or "grpc" or "httpupgrade")) node.Diagnostics.Add(new("node.transport", "该节点传输方式尚未实现等价转换。", ObjectId: node.Id));
        if (value.Map("smux").Boolean("enabled") || value.Boolean("smux") || value.Get("packet-encoding") is not null || value.Get("xhttp-opts") is not null || value.Get("h2-opts") is not null || value.Get("http-opts") is not null || value.Get("ws-opts") is YamlMappingNode wsOpts && (wsOpts.Get("max-early-data") is not null || wsOpts.Get("early-data-header-name") is not null)) node.Diagnostics.Add(new("node.unsupported-option", "节点包含尚未实现等价转换的传输选项。", ObjectId: node.Id));
        if (value.Get("udp") is not null && !value.Boolean("udp", true)) node.Diagnostics.Add(new("node.udp-disabled", "节点禁止 UDP 的语义尚未实现，未静默启用 UDP。", ObjectId: node.Id));
        var supportedKeys = new HashSet<string>(["<<", "name", "type", "server", "port", "uuid", "password", "username", "cipher", "encryption", "flow", "network", "tls", "skip-cert-verify", "servername", "sni", "client-fingerprint", "reality-opts", "alpn", "dialer-proxy", "ws-opts", "grpc-opts", "http-upgrade-opts", "udp", "alterId", "alter-id", "smux", "icon"], StringComparer.Ordinal);
        if (value.Keys().Any(key => !supportedKeys.Contains(key))) node.Diagnostics.Add(new("node.unknown-option", "节点包含未识别选项，不能确认等价行为。", ObjectId: node.Id));
        if (ws.Map("headers").Keys().Any(key => !key.Equals("Host", StringComparison.OrdinalIgnoreCase))) node.Diagnostics.Add(new("node.ws-headers", "自定义 WebSocket 头尚未实现等价转换。", ObjectId: node.Id));
        if (grpc.Keys().Any(key => key is not ("grpc-service-name" or "<<")) || reality.Keys().Any(key => key is not ("public-key" or "short-id" or "spider-x" or "support-x25519mlkem768" or "<<")) || ws.Keys().Any(key => key is not ("path" or "headers" or "<<"))) node.Diagnostics.Add(new("node.nested-option", "节点嵌套选项尚未实现等价转换。", ObjectId: node.Id));
        if (reality.Get("support-x25519mlkem768") is not null)
        {
            if (!reality.Boolean("support-x25519mlkem768")) node.Diagnostics.Add(new("node.reality-pq-disabled", "当前 Xray REALITY 无法等价禁用后量子握手支持。", ObjectId: node.Id));
            else node.Diagnostics.Add(new("node.reality-pq-auto", "后量子 REALITY 能力由固定版本 Xray 内置提供。", DiagnosticSeverity.Warning, node.Id));
        }
        node.Diagnostics.AddRange(PolicyValidator.ValidateNode(node));
        return node;
    }
    internal static string StableId(string kind, string source, string identity) => kind + "-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source + "\n" + identity)))[..24];
}
