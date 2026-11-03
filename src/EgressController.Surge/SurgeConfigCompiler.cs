using System.Globalization;
using System.Text;
using EgressController.Core.Routing;

namespace EgressController.Surge;

public sealed class SurgeConfigCompiler
{
    public string Compile(RoutingProfile profile, IReadOnlyList<ResolvedAdapter> liveAdapters, string? subscriptionProxySection = null)
    {
        if (profile.SchemaVersion != 1 || profile.Platform != "macos") throw new InvalidDataException("不支持的 macOS 配置版本。");
        var lines = new StringBuilder("#!name=EgressController\n# Managed profile; importing and activating are explicit user actions.\n[General]\nloglevel = warning\ndns-server = system\n\n[Proxy]\n");
        var adapters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var selected in profile.Adapters)
        {
            var live = liveAdapters.SingleOrDefault(a => a.Id == selected.Id);
            if (live is null || !live.Available) throw new InvalidOperationException($"网卡不可用：{selected.Label}；保留原选择。");
            if (live.InterfaceName.Length == 0 || !live.InterfaceName.All(char.IsAsciiLetterOrDigit)) throw new InvalidDataException("网卡名称无效。");
            string policy = $"EC-Adapter-{adapters.Count + 1}";
            if (!adapters.TryAdd(selected.Id, policy)) throw new InvalidDataException("重复网卡。");
            lines.AppendLine($"{policy} = direct, interface = {live.InterfaceName}, allow-other-interface = false");
        }
        var ports = new HashSet<int>();
        var bypass = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var port in profile.Ports)
        {
            if (port.Port is < 1 or > 65535 || !ports.Add(port.Port)) throw new InvalidDataException("本地端口无效或重复。");
            ValidatePath(port.OwnerPath, bundle: false);
            if (port.OwnerPath.Contains("/Surge.app/", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("不能将 Surge 自身的监听端口作为上游。");
            if (!adapters.TryGetValue(port.EgressAdapterId, out var egress)) throw new InvalidDataException("请为代理核心选择一个已连接的物理出口。");
            if (bypass.TryGetValue(port.OwnerPath, out var prior) && prior != egress) throw new InvalidDataException("同一代理核心不能绑定冲突的出口。");
            bypass[port.OwnerPath] = egress;
            lines.AppendLine($"EC-Local-{port.Port} = socks5, 127.0.0.1, {port.Port}");
        }
        var subscriptionNames = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(subscriptionProxySection))
        {
            // Only proxy definitions are imported; never General/Rule/Script/MITM sections.
            foreach (var raw in subscriptionProxySection.Split('\n'))
            {
                string line = raw.Trim(); if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
                int equal = line.IndexOf('=');
                if (equal < 1 || line.Any(char.IsControl)) throw new InvalidDataException("订阅代理定义无效。");
                string name = line[..equal].Trim(); ValidateAtom(name);
                if (name.StartsWith("EC-", StringComparison.Ordinal) || name is "DIRECT" or "REJECT" || !subscriptionNames.Add(name)) throw new InvalidDataException("订阅策略名称冲突。");
                lines.AppendLine(line);
            }
        }
        string Resolve(RouteDestination target) => target.Kind switch
        {
            "direct" when target.Id.Length == 0 => "DIRECT",
            "adapter" when adapters.TryGetValue(target.Id, out string? name) => name,
            "socks5" when int.TryParse(target.Id, NumberStyles.None, CultureInfo.InvariantCulture, out int port) && ports.Contains(port) => $"EC-Local-{port}",
            "subscription" when subscriptionNames.Contains(target.Id) => target.Id,
            _ => throw new InvalidDataException("规则引用的出口不存在，请重新选择。")
        };
        lines.AppendLine("\n[Rule]");
        foreach (var (path, policy) in bypass.OrderBy(x => x.Key, StringComparer.Ordinal)) lines.AppendLine($"PROCESS-NAME,{path},{policy}");
        var appPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var app in profile.Applications)
        {
            ValidatePath(app.Path, app.Bundle);
            string policy = Resolve(app.Target);
            if (appPaths.TryGetValue(app.Path, out var prior) && prior != policy) throw new InvalidDataException("应用有冲突的已保存出口。");
            if (bypass.TryGetValue(app.Path, out var ownerPolicy) && ownerPolicy != policy) throw new InvalidDataException("代理核心的应用路由与防循环出口冲突。");
            if (appPaths.TryAdd(app.Path, policy)) lines.AppendLine($"PROCESS-NAME,{app.Path},{policy}");
        }
        foreach (var rule in profile.Domains)
        {
            string domain;
            try { domain = new IdnMapping().GetAscii(rule.Domain.Trim().TrimEnd('.')); }
            catch (ArgumentException) { throw new InvalidDataException("域名无效。"); }
            if (Uri.CheckHostName(domain) != UriHostNameType.Dns || domain.Contains('*')) throw new InvalidDataException("请输入完整域名或后缀，不含通配符。");
            ValidateAtom(domain); lines.AppendLine($"DOMAIN-SUFFIX,{domain},{Resolve(rule.Target)}");
        }
        foreach (var rule in profile.RuleSets)
        {
            if (!Uri.TryCreate(rule.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0) throw new InvalidDataException("规则集必须使用 HTTPS 地址。");
            ValidateAtom(rule.Url); lines.AppendLine($"RULE-SET,{rule.Url},{Resolve(rule.Target)}");
        }
        lines.AppendLine($"FINAL,{Resolve(profile.DefaultRoute)}");
        return lines.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
    public static void ValidatePath(string path, bool bundle)
    {
        ValidateAtom(path);
        if (!path.StartsWith('/') || path.Contains('*') || path.Contains('?') || path.Split('/').Any(p => p is "." or "..") ||
            (bundle ? !path.EndsWith(".app/", StringComparison.Ordinal) : path.EndsWith('/')))
            throw new InvalidDataException("应用必须是规范的 .app 路径或原生可执行文件绝对路径。");
        if (!bundle && new[] { "node", "python", "python3", "bash", "zsh", "sh", "pwsh" }.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("不能为共享脚本解释器设置应用分流。");
    }
    private static void ValidateAtom(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(c => char.IsControl(c) || c is ',' or '#' or ';' or '=')) throw new InvalidDataException("配置字段包含不支持的分隔符。");
    }
}
