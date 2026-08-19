namespace EgressController.Core.Models;

/// <summary>
/// Full current-user System Proxy state read/written in one transaction (plan §1.8 / §12).
/// Server is the raw WinINet form (e.g. <c>http=127.0.0.1:18080;https=127.0.0.1:18080</c>);
/// ownership uses a semantic comparer, not raw string equality.
/// </summary>
public sealed record SystemProxyState(
    bool Enabled,
    string? Server,
    string? ProxyOverride,
    string? AutoConfigUrl,
    bool AutoDetect)
{
    public static readonly SystemProxyState Off = new(false, null, null, null, false);

    /// <summary>The exact state the Controller owns when in control (loopback routing proxy).</summary>
    public static SystemProxyState Ours(int localPort = 18080)
        => new(true, $"http=127.0.0.1:{localPort};https=127.0.0.1:{localPort}",
            "<local>;localhost;127.0.0.1", null, false);
}

/// <summary>
/// Semantic proxy-server equivalence for ownership checks: splits by scheme, normalizes
/// host (localhost == 127.0.0.1, IPv6 brackets), trims whitespace, and compares as maps.
/// </summary>
public static class SystemProxyStateComparer
{
    public static bool ServersEquivalent(string? a, string? b)
        => SameMap(NormalizeSchemeMap(a), NormalizeSchemeMap(b));

    public static bool StateEquivalent(SystemProxyState a, SystemProxyState b)
        => a.Enabled == b.Enabled
           && ServersEquivalent(a.Server, b.Server)
           && string.Equals(NormalizeBypass(a.ProxyOverride), NormalizeBypass(b.ProxyOverride), StringComparison.OrdinalIgnoreCase)
           && string.Equals(NormalizeUrl(a.AutoConfigUrl), NormalizeUrl(b.AutoConfigUrl), StringComparison.OrdinalIgnoreCase)
           && a.AutoDetect == b.AutoDetect;

    private static Dictionary<string, string> NormalizeSchemeMap(string? server)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(server))
            return map;
        foreach (string entry in server.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = entry.IndexOf('=');
            if (eq > 0)
                map[entry[..eq].Trim()] = NormalizeHost(entry[(eq + 1)..].Trim());
            else if (!map.ContainsKey("*"))
                map["*"] = NormalizeHost(entry);
        }
        return map;
    }

    private static string NormalizeHost(string hostport)
    {
        int colon = hostport.LastIndexOf(':');
        if (colon > 0)
        {
            string host = hostport[..colon].Trim('[', ']').ToLowerInvariant();
            if (host == "localhost") host = "127.0.0.1";
            return host + ":" + hostport[(colon + 1)..];
        }
        string only = hostport.Trim('[', ']').ToLowerInvariant();
        return only == "localhost" ? "127.0.0.1" : only;
    }

    private static bool SameMap(Dictionary<string, string> a, Dictionary<string, string> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kv in a)
            if (!b.TryGetValue(kv.Key, out string? v) || !string.Equals(kv.Value, v, StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }

    private static string NormalizeBypass(string? s)
        => string.IsNullOrWhiteSpace(s) ? string.Empty : string.Join(';', s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).OrderBy(x => x.ToLowerInvariant()));

    private static string NormalizeUrl(string? s)
        => (s?.Trim().TrimEnd('/') ?? string.Empty).ToLowerInvariant();
}