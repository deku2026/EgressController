using System.Net;
using System.Text.RegularExpressions;
using System.Text;

namespace EgressController.Policy;

public static partial class PolicyValidator
{
    public static IReadOnlyList<PolicyDiagnostic> Validate(MacPolicyDocument doc)
    {
        var errors = new List<PolicyDiagnostic>();
        if (doc.Version != 1) errors.Add(new("policy.version", "不支持该配置版本。"));
        if (string.IsNullOrWhiteSpace(doc.Interfaces.HomeId) || string.IsNullOrWhiteSpace(doc.Interfaces.NonHomeId) || doc.Interfaces.HomeId == doc.Interfaces.NonHomeId) errors.Add(new("interfaces.selection", "必须选择两张不同且具有稳定标识的物理网卡。"));
        if (doc.Defaults.China != ExitChoice.NonHome || doc.Defaults.ProxyInterface != "nonhome") errors.Add(new("defaults.fixed", "中国大陆必须非家宽直连，代理服务器必须绑定非家宽。"));
        if (doc.Defaults.Global.Kind != ExitKind.Proxy) errors.Add(new("defaults.global", "全球默认出口必须是代理节点或分组。"));
        if (!IPAddress.TryParse(doc.Dns.UpstreamIp, out var dnsIp) || IPAddress.IsLoopback(dnsIp) || dnsIp.Equals(IPAddress.Any) || dnsIp.Equals(IPAddress.IPv6Any)) errors.Add(new("dns.server", "DNS 上游必须是明确的非回环 IP 地址，不能依赖 DNS 解析自身。"));
        if (!DnsEndpoint.IsValid(doc.Dns.HttpsUrl)) errors.Add(new("dns.https", "加密 DNS 需要 HTTPS 上游地址，主机必须为公网 IP，不能包含凭据、查询或片段。"));
        if (doc.Defaults.ResourceProxyPort is < 1024 or > 65535) errors.Add(new("proxy.port", "Xray 本地 SOCKS5 端口必须在 1024–65535 之间。"));
        var tunCidr = doc.Tun.Ipv4Cidr.Split('/');
        if (!TunName().IsMatch(doc.Tun.Name) || doc.Tun.Mtu is < 1280 or > 9000 || tunCidr.Length != 2 || !IsCidr(doc.Tun.Ipv4Cidr) || !IPAddress.TryParse(tunCidr[0], out var tunIp) || tunIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || IPAddress.IsLoopback(tunIp) || tunIp.Equals(IPAddress.Any)) errors.Add(new("tun.settings", "TUN 需要明确的 utun 名称、IPv4 CIDR 以及 1280–9000 范围内的 MTU。"));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in doc.Nodes.Select(x => x.Id).Concat(doc.Groups.Select(x => x.Id)).Concat(doc.Providers.Select(x => x.Id)).Concat(doc.Applications.Select(x => x.Id)).Concat(doc.RuleSets.Select(x => x.Id)))
            if (!SafeId().IsMatch(id) || id is "blocked" or "home" or "nonhome" or "dns" or "tun" or "dns-query" || !ids.Add(id)) errors.Add(new("policy.id", "对象标识无效、保留或重复。"));
        var memberships = new Dictionary<string, ExitChoice>(StringComparer.Ordinal);
        foreach (var app in doc.Applications)
        {
            if (!IsExecutablePath(app.BundlePath, allowBundle: true)) errors.Add(new("application.bundle", "应用必须保存规范化的绝对 .app Bundle 路径，不能使用共享目录。", ObjectId: app.Id));
            if (app.ExecutablePaths.Count == 0 && app.Enabled) errors.Add(new("application.inventory", "应用尚未完成显式可执行文件清单扫描。", ObjectId: app.Id));
            foreach (var path in app.ExecutablePaths)
            {
                if (!IsExecutablePath(path)) errors.Add(new("application.path", "可执行清单必须是规范化绝对文件路径，不能是目录规则。", ObjectId: app.Id));
                if (!path.StartsWith(app.BundlePath + "/", StringComparison.Ordinal)) errors.Add(new("application.scope", "固定可执行路径必须位于该应用的准确 Bundle 根目录内；不能纳入外部解释器或共享目录。", ObjectId: app.Id));
                if (app.Enabled && memberships.TryGetValue(path, out var previous) && previous != app.Via) errors.Add(new("application.conflict", "同一可执行路径存在冲突出口，必须显式解决。", ObjectId: app.Id));
                if (app.Enabled) memberships[path] = app.Via;
            }
            if (app.PrimaryExecutable is { } primary && !app.ExecutablePaths.Contains(primary, StringComparer.Ordinal)) errors.Add(new("application.primary", "启动文件必须属于保存的可执行清单。", ObjectId: app.Id));
            if (app.Enabled) errors.AddRange(ValidateExit(app.Via, doc, app.Id));
        }
        var ruleTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in doc.RuleSets)
        {
            if (rule.Id.Length > 127 || !ruleTags.Add(rule.Id)) errors.Add(new("geodata.tag", "规则集合标识须在 127 个 ASCII 字符内且忽略大小写仍唯一。", ObjectId: rule.Id));
            foreach (var domain in rule.Domains) if (!DomainRuleValidation.IsValid(domain)) errors.Add(new("rule.domain", "域名规则无效；请填写域名而非 URL、路径或通配符。", ObjectId: rule.Id));
            foreach (var cidr in rule.IpCidrs) if (!IsCidr(cidr)) errors.Add(new("rule.cidr", "IP/CIDR 规则无效。", ObjectId: rule.Id));
            foreach (var source in rule.Sources) if (!SafeAssetSource(source)) errors.Add(new("rule.source", "规则来源必须是安全相对 YAML 路径。", ObjectId: rule.Id));
            if (rule.Enabled) errors.AddRange(ValidateExit(rule.Via, doc, rule.Id));
        }
        errors.AddRange(ValidateExit(doc.Defaults.Global, doc, "global"));
        foreach (var node in doc.Nodes) errors.AddRange(ValidateNode(node).Select(x => x with { Severity = DiagnosticSeverity.Warning }));
        foreach (var provider in doc.Providers) if (!YamlNodes.IsWebUrl(provider.Url) || string.IsNullOrWhiteSpace(provider.SourceId)) errors.Add(new("provider.definition", "节点提供器必须保存明确来源及有效 HTTP(S) URL。", DiagnosticSeverity.Warning, provider.Id));
        foreach (var source in doc.Subscriptions) if (!YamlNodes.IsWebUrl(source.Url)) errors.Add(new("subscription.url", "订阅必须使用不含 URL 用户信息的 HTTP(S) 地址。", ObjectId: source.Id));
        return errors.Distinct().ToList();
    }
    private static IEnumerable<PolicyDiagnostic> ValidateExit(ExitChoice exit, MacPolicyDocument doc, string id)
    {
        if (exit.Kind != ExitKind.Proxy) return [];
        var diagnostics = ValidateReachable(exit.TargetId ?? "", doc, new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), id).ToList();
        if (!doc.Groups.Any(group => group.Id == exit.TargetId)) diagnostics.Add(new("proxy.group-required", "代理出口必须引用分组；原有节点引用已保留，请明确选择分组后修复。", ObjectId: id));
        return diagnostics;
    }
    private static IEnumerable<PolicyDiagnostic> ValidateReachable(string target, MacPolicyDocument doc, HashSet<string> ancestors, HashSet<string> validated, string origin)
    {
        var errors = new List<PolicyDiagnostic>();
        if (ancestors.Count >= 128) return [new("proxy.depth", "代理嵌套深度超过安全限制。", ObjectId: origin)];
        if (!ancestors.Add(target)) return [new("proxy.cycle", "代理节点或分组存在循环引用。", ObjectId: origin)];
        if (validated.Contains(target)) { ancestors.Remove(target); return []; }
        if (target is GroupGraph.Direct or GroupGraph.Reject) { }
        else if (doc.Nodes.FirstOrDefault(x => x.Id == target) is { } node)
        {
            errors.AddRange(node.Diagnostics); errors.AddRange(ValidateNode(node));
            if (node.DialerProxyId is { } dialer)
            {
                errors.AddRange(ValidateReachable(dialer, doc, ancestors, validated, origin));
                if (GroupGraph.Leaves(dialer, doc).Any(id => id is GroupGraph.Direct or GroupGraph.Reject)) errors.Add(new("node.direct-dialer", "前置代理必须解析为节点，不能使用 DIRECT 或 REJECT。", ObjectId: node.Id));
            }
        }
        else if (doc.Groups.FirstOrDefault(x => x.Id == target) is { } group)
        {
            errors.AddRange(group.Diagnostics);
            if (group.Kind == GroupKind.Unsupported) errors.Add(new("group.unsupported", "所选分组无法等价运行。", ObjectId: origin));
            else if (group.Kind == GroupKind.Select)
            {
                if (group.SelectedMemberId is not { } selected || !group.MemberIds.Contains(selected, StringComparer.Ordinal)) errors.Add(new("group.selection", "手动分组必须显式选择一个有效成员。", ObjectId: group.Id));
                else errors.AddRange(ValidateReachable(selected, doc, ancestors, validated, origin));
            }
            else
            {
                if (group.MemberIds.Count == 0 || !YamlNodes.IsWebUrl(group.TestUrl) || group.IntervalSeconds < 1 || group.ToleranceMilliseconds < 0) errors.Add(new("group.test-settings", "自动分组成员或测速参数无效。", ObjectId: group.Id));
                foreach (var member in group.MemberIds) errors.AddRange(ValidateReachable(member, doc, ancestors, validated, origin));
            }
        }
        else errors.Add(new("proxy.missing", "所选代理对象不存在；保存的选择未被重置。", ObjectId: origin));
        ancestors.Remove(target); validated.Add(target); return errors;
    }
    internal static IEnumerable<PolicyDiagnostic> ValidateNode(ProxyNode node)
    {
        var errors = new List<PolicyDiagnostic>();
        void Add(string code, string message) => errors.Add(new(code, message, ObjectId: node.Id));
        if (node.Server.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(node.Server, out var address) && IPAddress.IsLoopback(address))
            Add("node.local-proxy", "节点不能引用本机代理中转；请选择订阅中的远端节点。");
        if (node.Protocol == NodeProtocol.Unsupported) Add("node.unsupported-protocol", "节点协议不受支持。");
        if (Uri.CheckHostName(node.Server) == UriHostNameType.Unknown || node.Port is < 1 or > 65535) Add("node.address", "节点地址或端口无效。");
        if (node.Protocol is NodeProtocol.Vless or NodeProtocol.Vmess && (string.IsNullOrWhiteSpace(node.UserId) || !Guid.TryParse(node.UserId, out _) && Encoding.UTF8.GetByteCount(node.UserId) >= 30)) Add("node.identity", "节点用户标识无效。");
        if (node.Protocol is NodeProtocol.Shadowsocks or NodeProtocol.Trojan && string.IsNullOrEmpty(node.Password)) Add("node.password", "节点缺少认证凭据。");
        if (node.Network is not ("tcp" or "ws" or "grpc" or "httpupgrade")) Add("node.transport", "该传输方式未实现等价转换。");
        if (node.Protocol is NodeProtocol.Socks or NodeProtocol.Http or NodeProtocol.Shadowsocks && node.Network != "tcp") Add("node.transport-protocol", "该协议不能使用指定传输。");
        if (node.Protocol == NodeProtocol.Vless)
        {
            if (!ValidVlessEncryption(node.Encryption)) Add("node.encryption", "VLESS encryption 模式、握手或认证参数无效。");
            if (node.Encryption == "none" && !node.Tls && node.RealityPublicKey.Length == 0) Add("node.security", "未加密 VLESS 必须启用 TLS/REALITY。");
            if (node.Flow is not ("" or "xtls-rprx-vision" or "xtls-rprx-vision-udp443")) Add("node.flow", "VLESS flow 参数不受支持。");
        }
        if (node.Protocol == NodeProtocol.Vmess && node.Security is not ("auto" or "aes-128-gcm" or "chacha20-poly1305")) Add("node.vmess-security", "该 VMess 加密方式无法等价转换。");
        if (node.Protocol == NodeProtocol.Shadowsocks && node.Cipher is not ("aes-128-gcm" or "aes-256-gcm" or "chacha20-poly1305" or "chacha20-ietf-poly1305" or "xchacha20-poly1305" or "xchacha20-ietf-poly1305" or "2022-blake3-aes-128-gcm" or "2022-blake3-aes-256-gcm" or "2022-blake3-chacha20-poly1305")) Add("node.ss-cipher", "该 Shadowsocks 加密方式不受 Xray 支持。");
        if (node.Protocol == NodeProtocol.Shadowsocks && node.Cipher.StartsWith("2022-", StringComparison.Ordinal) && !ValidShadowsocks2022Key(node.Cipher, node.Password)) Add("node.ss2022-key", "Shadowsocks 2022 需要规范 Base64 定长密钥；AES-128 每段 16 字节，其余每段 32 字节，ChaCha 不支持多密钥。");
        if (node.Plugin.Length > 0 || node.PluginMode.Length > 0 || node.PluginHost.Length > 0)
        {
            if (node.Protocol != NodeProtocol.Shadowsocks || node.Plugin != "obfs" || node.PluginMode != "http"
                || node.Network != "tcp" || node.Tls || node.RealityPublicKey.Length > 0)
                Add("node.plugin", "当前插件只支持 Shadowsocks 的 obfs/http 原生 RAW HTTP 伪装；其他模式不能等价替换。");
            if (node.PluginHost.Length is < 1 or > 253 || node.PluginHost.Any(char.IsControl)
                || !node.PluginHost.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-')
                || Uri.CheckHostName(node.PluginHost) is not (UriHostNameType.Dns or UriHostNameType.IPv4))
                Add("node.plugin-host", "HTTP 伪装 Host 必须为明确的 DNS 主机名或 IPv4 地址，不能包含端口、路径或注入字符。");
        }
        if (node.RealityPublicKey.Length > 0 && node.Protocol != NodeProtocol.Vless) Add("node.reality-protocol", "REALITY 当前只对 VLESS 导入开放。");
        if (node.RealityPublicKey.Length > 0 && node.Network is not ("tcp" or "grpc")) Add("node.reality-transport", "REALITY 与该传输不能等价组合。");
        if (node.RealityPublicKey.Length > 0 && (!RawBase64Length(node.RealityPublicKey, 32) || node.RealityShortId.Length > 16 || node.RealityShortId.Length % 2 != 0 || !node.RealityShortId.All(char.IsAsciiHexDigit) || node.RealitySpiderX.Length > 0 && !node.RealitySpiderX.StartsWith('/'))) Add("node.reality-parameters", "REALITY 认证参数或 spiderX 无效。");
        if (node.SkipCertificateVerify) Add("node.insecure-unsupported", "当前 Xray 已移除跳过证书验证选项，不能等价导入。");
        if (node.Protocol == NodeProtocol.Trojan && !node.Tls) Add("node.trojan-security", "Trojan 必须启用 TLS。");
        return errors;
    }
    internal static IEnumerable<PolicyDiagnostic> ValidateGraph(IEnumerable<ProxyNode> nodes, IEnumerable<ProxyGroup> groups)
    {
        var edges = nodes.ToDictionary(x => x.Id, x => x.DialerProxyId is { } dialer ? new List<string> { dialer } : [], StringComparer.Ordinal);
        foreach (var group in groups) edges[group.Id] = group.MemberIds;
        var visited = new HashSet<string>(StringComparer.Ordinal); var active = new HashSet<string>(StringComparer.Ordinal); var result = new List<PolicyDiagnostic>();
        void Visit(string id)
        {
            if (active.Count >= 128) { result.Add(new("proxy.depth", "代理嵌套深度超过安全限制。", ObjectId: id)); return; }
            if (active.Contains(id)) { result.Add(new("proxy.cycle", "订阅代理图存在循环引用。", ObjectId: id)); return; }
            if (!visited.Add(id)) return;
            active.Add(id); if (edges.TryGetValue(id, out var children)) foreach (var child in children) Visit(child); active.Remove(id);
        }
        foreach (var id in edges.Keys) Visit(id); return result;
    }
    public static bool IsExecutablePath(string path, bool allowBundle = false) => path.Length <= 4096 && path.StartsWith('/') && (allowBundle ? path.EndsWith(".app", StringComparison.OrdinalIgnoreCase) : !path.EndsWith('/') && !path.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) && !path.Any(char.IsControl) && !path.Split('/').Any(x => x is "." or "..") && !path.Contains("//", StringComparison.Ordinal);
    public static bool IsCidr(string value)
    {
        var parts = value.Split('/');
        if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var ip)) return false;
        return parts.Length == 1 || int.TryParse(parts[1], out var prefix) && prefix >= 0 && prefix <= (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
    }
    internal static bool SafeAssetSource(string source) => !string.IsNullOrWhiteSpace(source) && !Path.IsPathRooted(source) && !source.Contains(':') && !source.Split('/', '\\').Any(x => x is ".." or "." or "") && source.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase);
    private static bool ValidVlessEncryption(string value)
    {
        if (value == "none") return true;
        var blocks = value.Split('.');
        if (blocks.Length < 4 || blocks[0] != "mlkem768x25519plus" || blocks[1] is not ("native" or "xorpub" or "random") || blocks[2] is not ("0rtt" or "1rtt")) return false;
        var keySeen = false;
        for (var index = 3; index < blocks.Length; index++)
        {
            var block = blocks[index];
            if (block.Length >= 20)
            {
                if (!RawBase64Length(block, 32, 1184)) return false;
                keySeen = true;
            }
            else
            {
                if (keySeen || index == blocks.Length - 1) return false;
                var fields = block.Split('-');
                if (fields.Length != 3 || !int.TryParse(fields[0], out var probability) || probability is < 0 or > 100 || !int.TryParse(fields[1], out var min) || min < 0 || !int.TryParse(fields[2], out var max) || max < min) return false;
                if (index == 3 && (probability != 100 || min == 0)) return false;
            }
        }
        return keySeen;
    }
    private static bool RawBase64Length(string value, params int[] lengths)
    {
        if (value.Length % 4 == 1 || !value.All(x => char.IsAsciiLetterOrDigit(x) || x is '-' or '_')) return false;
        try { var bytes = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4)); return lengths.Contains(bytes.Length); }
        catch (FormatException) { return false; }
    }
    private static bool ValidShadowsocks2022Key(string cipher, string password)
    {
        var segments = password.Split(':');
        if (cipher == "2022-blake3-chacha20-poly1305" && segments.Length != 1) return false;
        int expectedLength = cipher == "2022-blake3-aes-128-gcm" ? 16 : 32;
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment.Any(char.IsWhiteSpace)) return false;
            try
            {
                var bytes = Convert.FromBase64String(segment);
                if (bytes.Length != expectedLength || !string.Equals(Convert.ToBase64String(bytes), segment, StringComparison.Ordinal)) return false;
            }
            catch (FormatException) { return false; }
        }
        return true;
    }
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$", RegexOptions.CultureInvariant)] private static partial Regex SafeId();
    [GeneratedRegex("^utun[0-9]{1,4}$", RegexOptions.CultureInvariant)] private static partial Regex TunName();
}
