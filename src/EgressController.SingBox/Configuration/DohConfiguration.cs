namespace EgressController.SingBox.Configuration;

public enum DohRoutePlane
{
    Esim,
    Clash,
}

public sealed record SingBoxDohEndpointDefinition(
    string Tag,
    DohRoutePlane RoutePlane,
    string Provider,
    bool IsFallback,
    string Server,
    int ServerPort,
    string Path,
    string ServerName,
    string Detour,
    string ProbeSuffix)
{
    public string RoutePlaneLabel => RoutePlane switch
    {
        DohRoutePlane.Esim => "eSIM 出口",
        DohRoutePlane.Clash => "7890 出口",
        _ => RoutePlane.ToString(),
    };

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
    public string EsimDnsTag { get; init; } = EgressDohConfiguration.EsimCloudflareTag;
    public string ClashDnsTag { get; init; } = EgressDohConfiguration.ClashCloudflareTag;
    public bool FailClosed { get; init; }

    public static DohRoutingDecision Default { get; } = new();
}

public static class EgressDohConfiguration
{
    public const string EsimCloudflareTag = "dns-esim";
    public const string EsimDnsPodTag = "dns-esim-backup";
    public const string ClashCloudflareTag = "dns-clash";
    public const string ClashDnsPodTag = "dns-clash-backup";

    public static IReadOnlyList<SingBoxDohEndpointDefinition> Endpoints { get; } =
    [
        new(
            EsimCloudflareTag,
            DohRoutePlane.Esim,
            "Cloudflare",
            IsFallback: false,
            "cloudflare-dns.com",
            443,
            "/dns-query",
            "cloudflare-dns.com",
            EgressProfileCompiler.EsimDirectTag,
            "doh-esim-cloudflare.egresscontroller.invalid"),
        new(
            EsimDnsPodTag,
            DohRoutePlane.Esim,
            "腾讯 DNSPod",
            IsFallback: true,
            "doh.pub",
            443,
            "/dns-query",
            "doh.pub",
            EgressProfileCompiler.EsimDirectTag,
            "doh-esim-dnspod.egresscontroller.invalid"),
        new(
            ClashCloudflareTag,
            DohRoutePlane.Clash,
            "Cloudflare",
            IsFallback: false,
            "cloudflare-dns.com",
            443,
            "/dns-query",
            "cloudflare-dns.com",
            EgressProfileCompiler.UpstreamSocksTag,
            "doh-clash-cloudflare.egresscontroller.invalid"),
        new(
            ClashDnsPodTag,
            DohRoutePlane.Clash,
            "腾讯 DNSPod",
            IsFallback: true,
            "doh.pub",
            443,
            "/dns-query",
            "doh.pub",
            EgressProfileCompiler.UpstreamSocksTag,
            "doh-clash-dnspod.egresscontroller.invalid"),
    ];

    public static DohRoutingDecision Decide(
        IReadOnlyList<DohProbeResult> probes,
        bool esimReady,
        DohRoutingDecision current)
    {
        ArgumentNullException.ThrowIfNull(probes);
        ArgumentNullException.ThrowIfNull(current);

        bool esimHasHealthyEndpoint = !esimReady || HasHealthy(probes, DohRoutePlane.Esim);
        bool clashHasHealthyEndpoint = HasHealthy(probes, DohRoutePlane.Clash);
        string esimTag = esimReady
            ? SelectTag(probes, DohRoutePlane.Esim, current.EsimDnsTag)
            : current.EsimDnsTag;
        string clashTag = SelectTag(probes, DohRoutePlane.Clash, current.ClashDnsTag);

        return new DohRoutingDecision
        {
            EsimDnsTag = esimTag,
            ClashDnsTag = clashTag,
            FailClosed = !esimHasHealthyEndpoint || !clashHasHealthyEndpoint,
        };
    }

    public static bool IsAvailable(
        SingBoxDohEndpointDefinition endpoint,
        bool esimReady)
        => endpoint.RoutePlane != DohRoutePlane.Esim || esimReady;

    public static SingBoxDohEndpointDefinition? Find(string tag)
        => Endpoints.FirstOrDefault(endpoint => string.Equals(endpoint.Tag, tag, StringComparison.Ordinal));

    private static bool HasHealthy(
        IReadOnlyList<DohProbeResult> probes,
        DohRoutePlane plane)
        => Endpoints
            .Where(endpoint => endpoint.RoutePlane == plane)
            .Any(endpoint => probes.Any(probe =>
                string.Equals(probe.Tag, endpoint.Tag, StringComparison.Ordinal)
                && probe.IsHealthy));

    private static string SelectTag(
        IReadOnlyList<DohProbeResult> probes,
        DohRoutePlane plane,
        string currentTag)
    {
        SingBoxDohEndpointDefinition[] candidates = Endpoints
            .Where(endpoint => endpoint.RoutePlane == plane)
            .ToArray();
        SingBoxDohEndpointDefinition? current = candidates.FirstOrDefault(endpoint =>
            string.Equals(endpoint.Tag, currentTag, StringComparison.Ordinal));
        if (current is not null && IsHealthy(probes, current.Tag))
            return current.Tag;

        return candidates.FirstOrDefault(endpoint => IsHealthy(probes, endpoint.Tag))?.Tag
            ?? current?.Tag
            ?? candidates[0].Tag;
    }

    private static bool IsHealthy(IReadOnlyList<DohProbeResult> probes, string tag)
        => probes.Any(probe =>
            string.Equals(probe.Tag, tag, StringComparison.Ordinal)
            && probe.IsHealthy);
}
