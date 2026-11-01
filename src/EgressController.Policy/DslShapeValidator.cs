using YamlDotNet.RepresentationModel;

namespace EgressController.Policy;

internal static class DslShapeValidator
{
    internal static List<PolicyDiagnostic> Validate(YamlMappingNode root)
    {
        var errors = new List<PolicyDiagnostic>();
        void Mapping(YamlMappingNode map, string[] allowed, string[]? sequences = null, string[]? maps = null, string[]? booleans = null, string[]? integers = null)
        {
            foreach (var (keyNode, value) in map.Children)
            {
                var key = ((YamlScalarNode)keyNode).Value ?? "";
                if (key == "<<") continue;
                if (!allowed.Contains(key, StringComparer.Ordinal)) { errors.Add(new("dsl.unknown-field", "配置存在无法识别的字段；未静默忽略。")); continue; }
                if (sequences?.Contains(key, StringComparer.Ordinal) == true)
                { if (value is not YamlSequenceNode) errors.Add(new("dsl.sequence", "配置中的数组字段类型无效。")); }
                else if (maps?.Contains(key, StringComparer.Ordinal) == true)
                { if (value is not YamlMappingNode) errors.Add(new("dsl.mapping", "配置中的映射字段类型无效。")); }
                else if (key is "via" or "china" or "global")
                { if (value is YamlMappingNode exit) Mapping(exit, ["proxy", "proxy_group"]); else if (value is not YamlScalarNode { Value: "home" or "nonhome" }) errors.Add(new("dsl.exit", "出口必须为 home、nonhome 或明确的代理引用。")); }
                else if (value is not YamlScalarNode scalar) errors.Add(new("dsl.scalar", "配置中的标量字段类型无效。"));
                else if (booleans?.Contains(key, StringComparer.Ordinal) == true && !bool.TryParse(scalar.Value, out _)) errors.Add(new("dsl.boolean", "布尔字段必须为 true 或 false。"));
                else if (integers?.Contains(key, StringComparer.Ordinal) == true && !int.TryParse(scalar.Value, System.Globalization.CultureInfo.InvariantCulture, out _)) errors.Add(new("dsl.integer", "整数字段无效。"));
            }
        }
        Mapping(root, ["version", "interfaces", "defaults", "dns", "tun", "applications", "rule_sets", "nodes", "groups", "providers", "subscriptions"], ["applications", "rule_sets", "nodes", "groups", "providers", "subscriptions"], ["interfaces", "defaults", "dns", "tun"], integers: ["version"]);
        Mapping(root.Map("interfaces"), ["home", "nonhome"]);
        Mapping(root.Map("defaults"), ["china", "global", "proxy_interface", "resource_proxy_port"], integers: ["resource_proxy_port"]);
        Mapping(root.Map("dns"), ["upstream_ip"]);
        Mapping(root.Map("tun"), ["name", "ipv4_cidr", "mtu"], integers: ["mtu"]);
        void Rows(string field, string[] allowed, string[]? sequences = null, string[]? booleans = null, string[]? integers = null)
        {
            foreach (var node in root.Items(field))
                if (node is YamlMappingNode map) Mapping(map, allowed, sequences, booleans: booleans, integers: integers);
                else errors.Add(new("dsl.row", "数组元素必须是映射。"));
        }
        Rows("applications", ["id", "name", "path", "bundle_id", "primary_executable", "executables", "via", "enabled"], ["executables"], ["enabled"]);
        Rows("rule_sets", ["id", "name", "sources", "domains", "ip_cidrs", "via", "enabled"], ["sources", "domains", "ip_cidrs"], ["enabled"]);
        Rows("nodes", ["id", "name", "source_id", "protocol", "server", "port", "user_id", "password", "username", "cipher", "encryption", "flow", "security", "network", "tls", "skip_certificate_verify", "server_name", "fingerprint", "reality_public_key", "reality_short_id", "reality_spider_x", "alpn", "transport_path", "transport_host", "grpc_service_name", "plugin", "plugin_mode", "plugin_host", "tcp_fast_open", "dialer_proxy", "diagnostics"], ["alpn", "diagnostics"], ["tls", "skip_certificate_verify", "tcp_fast_open"], ["port"]);
        Rows("groups", ["id", "name", "source_id", "kind", "retained", "strategy_override", "icon", "members", "providers", "selected", "test_url", "interval", "tolerance", "diagnostics"], ["members", "providers", "diagnostics"], booleans: ["retained", "strategy_override"], integers: ["interval", "tolerance"]);
        Rows("subscriptions", ["id", "name", "url"]);
        Rows("providers", ["id", "name", "source_id", "url", "diagnostics"], ["diagnostics"]);
        foreach (var rule in root.Items("rule_sets").OfType<YamlMappingNode>())
            foreach (var domain in rule.Items("domains"))
                if (domain is YamlMappingNode map) Mapping(map, ["kind", "value"]); else errors.Add(new("dsl.domain", "域名条目必须包含匹配类型和值。"));
        foreach (var field in new[] { "applications", "rule_sets", "nodes", "groups" })
            foreach (var row in root.Items(field).OfType<YamlMappingNode>())
                foreach (var key in new[] { "executables", "sources", "ip_cidrs", "alpn", "members", "providers" })
                    if (row.Items(key).Any(x => x is not YamlScalarNode)) errors.Add(new("dsl.string-list", "字符串数组包含非标量元素。"));
        return errors.Distinct().ToList();
    }
}
