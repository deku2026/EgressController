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
    public string RoutePlaneLabel => "全局 DNS · ESIM-家宽";

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

    public static DohRoutingDecision Default { get; } = new();
}

public static class EgressDohConfiguration
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

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
            EgressProfileCompiler.DnsDirectTag,
            "doh-global-cloudflare.egresscontroller.invalid"),
        new(
            DnsPodTag,
            "腾讯 DNSPod",
            IsFallback: true,
            "doh.pub",
            443,
            "/dns-query",
            "doh.pub",
            EgressProfileCompiler.DnsDirectTag,
            "doh-global-dnspod.egresscontroller.invalid"),
    ];

    public static DohRoutingDecision Decide(
        IReadOnlyList<DohProbeResult> probes,
        bool dnsReady,
        DohRoutingDecision current)
    {
        ArgumentNullException.ThrowIfNull(probes);
        ArgumentNullException.ThrowIfNull(current);

        bool cloudflare = dnsReady && probes.Any(probe => probe.Tag == CloudflareTag && probe.IsHealthy);
        bool dnspod = dnsReady && probes.Any(probe => probe.Tag == DnsPodTag && probe.IsHealthy);
        string dnsTag = cloudflare ? CloudflareTag : dnspod ? DnsPodTag : current.DnsTag;

        return new DohRoutingDecision
        {
            DnsTag = dnsTag,
        };
    }

    public static bool IsAvailable(
        SingBoxDohEndpointDefinition endpoint,
        bool dnsReady)
        => dnsReady;

    public static SingBoxDohEndpointDefinition? Find(string tag)
        => Endpoints.FirstOrDefault(endpoint => string.Equals(endpoint.Tag, tag, StringComparison.Ordinal));

}
