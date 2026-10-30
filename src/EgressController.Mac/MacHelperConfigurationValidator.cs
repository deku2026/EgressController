// SPDX-License-Identifier: MIT
using System.Net;
using System.Text.Json;

namespace EgressController.Mac;

public sealed record MacConfigurationValidation(bool IsValid, string Code, string Message);

/// <summary>Independent, pure allow-list for the small configuration shape admitted to a future helper.</summary>
public static class MacHelperConfigurationValidator
{
    private static readonly HashSet<string> ProxyProtocols = new(["vless", "vmess", "trojan", "shadowsocks", "socks", "http"], StringComparer.Ordinal);

    public static MacConfigurationValidation Validate(ReadOnlyMemory<byte> utf8Json, string homeInterface, string nonHomeInterface)
    {
        try
        {
            Need(utf8Json.Length is > 0 and <= MacHelperRequestValidator.MaximumFrameBytes, "configuration-size", "Configuration exceeds its size boundary.");
            Need(IsInterface(homeInterface) && IsInterface(nonHomeInterface) && homeInterface != nonHomeInterface,
                "selected-interfaces", "Two distinct current physical interfaces are required.");
            using var document = JsonDocument.Parse(utf8Json, new JsonDocumentOptions { MaxDepth = 32 });
            UniqueProperties(document.RootElement);
            JsonElement root = Object(document.RootElement, "log", "dns", "inbounds", "outbounds", "routing");
            if (root.TryGetProperty("log", out var log)) Object(log, "loglevel"); // No arbitrary access/error output file paths.
            JsonElement dns = Object(root.GetProperty("dns"), "tag", "queryStrategy", "disableFallback", "servers");
            Need(Text(dns, "tag") == "dns-query" && Text(dns, "queryStrategy") == "UseIP" && dns.GetProperty("disableFallback").ValueKind == JsonValueKind.True,
                "dns-fallback", "DNS fallback and implicit resolver selection are forbidden.");
            var servers = Array(dns.GetProperty("servers"));
            Need(servers.GetArrayLength() > 0 && servers.EnumerateArray().All(server => server.ValueKind == JsonValueKind.String
                && IPAddress.TryParse(server.GetString(), out var address) && !IPAddress.IsLoopback(address)
                && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any)),
                "dns-upstream", "Bootstrap DNS servers must be explicit IP addresses.");

            JsonElement inbounds = Array(root.GetProperty("inbounds"));
            Need(inbounds.GetArrayLength() == 1, "inbound-count", "Exactly one native TUN inbound is admitted.");
            JsonElement inbound = Object(inbounds[0], "tag", "protocol", "settings", "sniffing");
            Need(Text(inbound, "protocol") == "tun" && Text(inbound, "tag") == "tun", "inbound-protocol", "Local HTTP/SOCKS/control listeners are forbidden.");
            JsonElement tun = Object(inbound.GetProperty("settings"), "name", "mtu", "gateway", "autoSystemRoutingTable", "autoOutboundsInterface");
            string tunName = Text(tun, "name");
            Need(tunName.StartsWith("utun", StringComparison.Ordinal) && tunName.Length is > 4 and < 16 && tunName.AsSpan(4).ToArray().All(char.IsAsciiDigit),
                "tun-name", "The TUN name must be a bounded utun interface name.");
            Need(Text(tun, "autoOutboundsInterface") == "", "tun-interface", "The global automatic interface controller must be explicitly disabled; every outbound binds its own interface.");
            Need(tun.GetProperty("mtu").TryGetInt32(out int mtu) && mtu is >= 1280 and <= 9000, "tun-mtu", "TUN MTU is outside its admitted range.");
            var systemRoutes = Array(tun.GetProperty("autoSystemRoutingTable")).EnumerateArray().Select(item => item.GetString()).ToArray();
            Need(systemRoutes.Length == 2 && systemRoutes.Contains("0.0.0.0/0", StringComparer.Ordinal) && systemRoutes.Contains("::/0", StringComparer.Ordinal),
                "tun-routes", "The TUN route plan must explicitly cover IPv4 and IPv6.");
            var gateways = Array(tun.GetProperty("gateway"));
            Need(gateways.GetArrayLength() > 0 && gateways.EnumerateArray().All(item => IsCidr(item.GetString())), "tun-gateway", "Invalid TUN gateway CIDR.");
            if (inbound.TryGetProperty("sniffing", out var sniffing)) Object(sniffing, "enabled", "destOverride", "routeOnly");

            var outbounds = Array(root.GetProperty("outbounds"));
            Need(outbounds.GetArrayLength() is >= 4 and <= 4096, "outbound-count", "Configuration has an invalid outbound count.");
            var protocols = new Dictionary<string, string>(StringComparer.Ordinal);
            var dialers = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonElement item in outbounds.EnumerateArray())
            {
                JsonElement outbound = Object(item, "tag", "protocol", "settings", "streamSettings");
                string tag = Text(outbound, "tag"), protocol = Text(outbound, "protocol");
                Need(tag.Length is > 0 and <= 128 && protocols.TryAdd(tag, protocol), "outbound-identity", "Outbound tags must be nonempty and unique.");
                JsonElement settings = outbound.GetProperty("settings");
                if (protocol == "blackhole")
                {
                    Need(tag == "blocked", "block-outbound", "Only the explicit blocked outbound may drop traffic.");
                    Object(settings);
                    Need(!outbound.TryGetProperty("streamSettings", out _), "block-dialer", "A blocked outbound cannot contain a dialer.");
                    continue;
                }
                Need(protocol is "freedom" or "dns" || ProxyProtocols.Contains(protocol), "outbound-protocol", "Unsupported or dynamic outbound protocol.");
                if (protocol == "freedom")
                {
                    Need(tag is "home" or "nonhome", "direct-fallback", "Additional direct/fallback outbounds are forbidden.");
                    Object(settings); // Prevent redirect, target rewriting or hidden fallback behavior.
                }
                else if (protocol == "dns")
                {
                    Need(tag == "dns", "dns-outbound", "The DNS outbound must use its reserved tag.");
                    Object(settings, "rules");
                    foreach (var rule in Array(settings.GetProperty("rules")).EnumerateArray()) Object(rule, "action", "qType", "rCode");
                }
                else
                {
                    Need(tag is not ("home" or "nonhome" or "dns" or "blocked"), "reserved-tag", "Proxy nodes cannot replace reserved system outbounds.");
                    Object(settings, "address", "port", "id", "encryption", "flow", "security", "method", "password", "user", "pass");
                    string address = Text(settings, "address");
                    Need(!string.IsNullOrWhiteSpace(address) && address.Length <= 1024 && !address.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                        && (!IPAddress.TryParse(address, out var ip) || !IPAddress.IsLoopback(ip)), "local-proxy", "A node cannot reference a local proxy relay.");
                    Need(settings.GetProperty("port").TryGetInt32(out int port) && port is > 0 and <= 65535, "node-port", "Invalid remote node port.");
                }
                JsonElement stream = Object(outbound.GetProperty("streamSettings"), "sockopt", "network", "security", "tlsSettings", "realitySettings", "wsSettings", "grpcSettings", "httpupgradeSettings");
                JsonElement socket = Object(stream.GetProperty("sockopt"), "interface", "domainStrategy", "dialerProxy");
                string expectedInterface = protocol == "freedom" && tag == "home" ? homeInterface : nonHomeInterface;
                Need(Text(socket, "interface") == expectedInterface && Text(socket, "domainStrategy") == "ForceIP", "outbound-interface", "Every dialer must bind its assigned physical interface with no automatic fallback.");
                if (socket.TryGetProperty("dialerProxy", out var dialer))
                {
                    Need(ProxyProtocols.Contains(protocol), "direct-dialer", "System/direct outbounds cannot inherit a proxy dialer.");
                    dialers[tag] = dialer.GetString() ?? "";
                }
                if (stream.TryGetProperty("network", out var network)) Need(network.GetString() is "raw" or "ws" or "grpc" or "httpupgrade", "transport", "Unsupported proxy transport.");
                if (stream.TryGetProperty("security", out var security)) Need(security.GetString() is "tls" or "reality" or "none", "stream-security", "Unsupported stream security.");
                if (stream.TryGetProperty("tlsSettings", out var tls)) Object(tls, "serverName", "allowInsecure", "alpn", "fingerprint");
                if (stream.TryGetProperty("realitySettings", out var reality)) Object(reality, "serverName", "fingerprint", "password", "shortId", "spiderX");
                if (stream.TryGetProperty("wsSettings", out var ws)) Object(ws, "path", "host");
                if (stream.TryGetProperty("grpcSettings", out var grpc)) Object(grpc, "serviceName");
                if (stream.TryGetProperty("httpupgradeSettings", out var upgrade)) Object(upgrade, "path", "host");
            }
            Need(Text(outbounds[0], "tag") == "blocked" && protocols.GetValueOrDefault("blocked") == "blackhole"
                && protocols.GetValueOrDefault("home") == "freedom" && protocols.GetValueOrDefault("nonhome") == "freedom" && protocols.GetValueOrDefault("dns") == "dns",
                "system-outbounds", "Required system outbounds or the deny-by-default first outbound are missing.");
            foreach (var (source, _) in dialers)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal) { source };
                string cursor = source;
                while (dialers.TryGetValue(cursor, out var next))
                {
                    Need(protocols.TryGetValue(next, out var protocol) && ProxyProtocols.Contains(protocol) && seen.Add(next),
                        "dialer-reference", "Dialer references must resolve to proxy nodes without cycles or direct fallback.");
                    cursor = next;
                }
            }

            JsonElement routing = Object(root.GetProperty("routing"), "domainStrategy", "rules");
            Need(Text(routing, "domainStrategy") == "IPOnDemand", "routing-strategy", "The explicit catch-all requires IPOnDemand routing.");
            JsonElement rules = Array(routing.GetProperty("rules"));
            Need(rules.GetArrayLength() is >= 5 and <= 100_000, "routing-rules", "Required routing stages are missing.");
            bool dnsRouted = false;
            int ruleIndex = 0, dnsIndex = -1, hijackIndex = -1, chinaStages = 0;
            var ruleTags = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement item in rules.EnumerateArray())
            {
                JsonElement rule = Object(item, "ruleTag", "outboundTag", "port", "inboundTag", "ip", "process", "domain", "network");
                string target = Text(rule, "outboundTag");
                Need(protocols.ContainsKey(target), "route-reference", "A route references an absent outbound.");
                string ruleTag = Text(rule, "ruleTag");
                Need(ruleTags.Add(ruleTag), "route-identity", "Route tags must be unique.");
                if (ruleIndex < rules.GetArrayLength() - 1)
                    Need(new[] { "domain", "ip", "process", "port", "inboundTag" }.Any(field => rule.TryGetProperty(field, out _)),
                        "direct-fallback", "Only the final proxy rule may be an unconditional catch-all.");
                if (rule.TryGetProperty("process", out var process))
                    Need(Array(process).EnumerateArray().All(path => path.ValueKind == JsonValueKind.String && MacPath.IsAbsolute(path.GetString())
                        && !MacPath.HasTraversal(path.GetString()!) && !path.GetString()!.EndsWith('/')),
                        "process-membership", "Process routing must contain fixed absolute executable paths, never names or directories.");
                foreach (string key in new[] { "domain", "ip" })
                    if (rule.TryGetProperty(key, out var references))
                        foreach (var reference in Array(references).EnumerateArray())
                            Need(SafeDatReference(reference.GetString()), "rule-file-reference", "Rule data references must remain single owned DAT filenames.");
                if (ruleTag == "dns-upstream")
                {
                    dnsRouted = target == "nonhome" && Array(rule.GetProperty("inboundTag")).EnumerateArray().Any(tag => tag.GetString() == "dns-query");
                    dnsIndex = ruleIndex;
                }
                if (ruleTag == "system-dns")
                {
                    Need(target == "dns" && rule.GetProperty("port").TryGetInt32(out int port) && port == 53, "dns-hijack", "System DNS must route to the DNS outbound.");
                    hijackIndex = ruleIndex;
                }
                if (ruleTag is "china-domains" or "china-ips")
                { Need(target == "nonhome", "china-route", "Built-in China rules must use non-home direct."); chinaStages++; }
                ruleIndex++;
            }
            JsonElement final = rules[rules.GetArrayLength() - 1];
            Need(Text(final, "ruleTag") == "global" && Text(final, "network") == "tcp,udp"
                && protocols.TryGetValue(Text(final, "outboundTag"), out var finalProtocol) && ProxyProtocols.Contains(finalProtocol)
                && final.EnumerateObject().Count() == 3 && dnsRouted && dnsIndex >= 0 && hijackIndex > dnsIndex && chinaStages == 2,
                "global-fallback", "The final TCP/UDP route must be an explicit proxy and DNS must be bound to non-home direct.");
            return new(true, "ok", "Configuration satisfies the admitted helper boundary.");
        }
        catch (Rejected error) { return new(false, error.Code, error.Message); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        { return new(false, "configuration-shape", "Configuration is malformed or does not match the admitted generated shape."); }
    }

    private static bool IsInterface(string value) => value.Length is > 0 and <= 16 && value[0] is >= 'a' and <= 'z'
        && value.All(character => char.IsAsciiLetterOrDigit(character)) && value.Any(char.IsAsciiDigit)
        && !value.StartsWith("utun", StringComparison.Ordinal) && value != "lo0";
    private static bool SafeDatReference(string? value)
    {
        if (value is null) return false;
        if (!value.StartsWith("ext:", StringComparison.Ordinal)) return !value.StartsWith("ext", StringComparison.OrdinalIgnoreCase) || !value.Contains(':');
        var parts = value.Split(':');
        return parts.Length == 3 && parts[1].EndsWith(".dat", StringComparison.Ordinal) && parts[1].Length > 4
            && parts[1].All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')
            && parts[2].Length > 0 && parts[2].All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
    }
    private static bool IsCidr(string? value)
    {
        if (value is null) return false;
        var parts = value.Split('/');
        return parts.Length == 2 && IPAddress.TryParse(parts[0], out var ip) && int.TryParse(parts[1], out int prefix)
            && prefix >= 0 && prefix <= (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
    }
    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString() ?? "";
    private static JsonElement Array(JsonElement value)
    { Need(value.ValueKind == JsonValueKind.Array, "configuration-shape", "An array was expected."); return value; }
    private static JsonElement Object(JsonElement value, params string[] allowed)
    {
        Need(value.ValueKind == JsonValueKind.Object, "configuration-shape", "An object was expected.");
        foreach (var property in value.EnumerateObject()) Need(allowed.Contains(property.Name, StringComparer.Ordinal), "unsupported-field", "An unadmitted configuration field was rejected.");
        return value;
    }
    private static void UniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array) { foreach (var item in value.EnumerateArray()) UniqueProperties(item); }
        if (value.ValueKind != JsonValueKind.Object) return;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateObject())
        { Need(keys.Add(item.Name), "duplicate-field", "Duplicate JSON fields are forbidden."); UniqueProperties(item.Value); }
    }
    private static void Need(bool accepted, string code, string message) { if (!accepted) throw new Rejected(code, message); }
    private sealed class Rejected(string code, string message) : Exception(message) { internal string Code { get; } = code; }
}
