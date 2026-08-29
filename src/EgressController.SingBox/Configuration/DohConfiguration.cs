namespace EgressController.SingBox.Configuration;

public sealed record SingBoxDohEndpointDefinition(
    string Tag,
    string Provider,
    bool IsFallback,
    string Server,
    int ServerPort,
    string Path,
    string ServerName,
    string Detour,
    string ProbeSuffix)
{
    public string RoutePlaneLabel => "全局 DNS · eSIM 出口";

    public string CreateProbeHost(string nonce)
    {
        if (string.IsNullOrWhiteSpace(nonce))
            throw new ArgumentException("A probe nonce is required.", nameof(nonce));
        return $"health-{nonce.Trim().ToLowerInvariant()}.{ProbeSuffix}";
    }
}

public sealed record DohProbeResult(
    string Tag,
    bool IsHealthy,
    string? Detail = null,
    int? DnsStatus = null,
    long? LatencyMilliseconds = null);

public sealed record DohRoutingDecision
{
    public string DnsTag { get; init; } = EgressDohConfiguration.CloudflareTag;
    public bool FailClosed { get; init; }

    public static DohRoutingDecision Default { get; } = new();
}

public static class EgressDohConfiguration
{
    public const string CloudflareTag = "dns-global";
    public const string DnsPodTag = "dns-global-backup";

    public static IReadOnlyList<SingBoxDohEndpointDefinition> Endpoints { get; } =
    [
        new(
            CloudflareTag,
            "Cloudflare",
            IsFallback: false,
            "cloudflare-dns.com",
            443,
            "/dns-query",
            "cloudflare-dns.com",
            EgressProfileCompiler.EsimDirectTag,
            "doh-global-cloudflare.egresscontroller.invalid"),
        new(
            DnsPodTag,
            "腾讯 DNSPod",
            IsFallback: true,
            "doh.pub",
            443,
            "/dns-query",
            "doh.pub",
            EgressProfileCompiler.EsimDirectTag,
            "doh-global-dnspod.egresscontroller.invalid"),
    ];

    public static DohRoutingDecision Decide(
        IReadOnlyList<DohProbeResult> probes,
        bool esimReady,
        DohRoutingDecision current)
    {
        ArgumentNullException.ThrowIfNull(probes);
        ArgumentNullException.ThrowIfNull(current);

        string dnsTag = SelectTag(probes, current.DnsTag);
        bool hasHealthyEndpoint = esimReady && HasHealthy(probes);

        return new DohRoutingDecision
        {
            DnsTag = dnsTag,
            FailClosed = !hasHealthyEndpoint,
        };
    }

    public static bool IsAvailable(
        SingBoxDohEndpointDefinition endpoint,
        bool esimReady)
        => esimReady;

    public static SingBoxDohEndpointDefinition? Find(string tag)
        => Endpoints.FirstOrDefault(endpoint => string.Equals(endpoint.Tag, tag, StringComparison.Ordinal));

    private static bool HasHealthy(IReadOnlyList<DohProbeResult> probes)
        => Endpoints
            .Any(endpoint => probes.Any(probe =>
                string.Equals(probe.Tag, endpoint.Tag, StringComparison.Ordinal)
                && probe.IsHealthy));

    private static string SelectTag(
        IReadOnlyList<DohProbeResult> probes,
        string currentTag)
    {
        // Endpoint order is priority order: return to Cloudflare automatically after recovery.
        return Endpoints.FirstOrDefault(endpoint => IsHealthy(probes, endpoint.Tag))?.Tag
            ?? Find(currentTag)?.Tag
            ?? CloudflareTag;
    }

    private static bool IsHealthy(IReadOnlyList<DohProbeResult> probes, string tag)
        => probes.Any(probe =>
            string.Equals(probe.Tag, tag, StringComparison.Ordinal)
            && probe.IsHealthy);
}
