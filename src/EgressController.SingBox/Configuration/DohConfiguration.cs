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

public static class EgressDohConfiguration
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    public const string CloudflareTag = "dns-global";

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
    ];

    public static bool IsAvailable(
        SingBoxDohEndpointDefinition endpoint,
        bool dnsReady)
        => dnsReady;

    public static SingBoxDohEndpointDefinition? Find(string tag)
        => Endpoints.FirstOrDefault(endpoint => string.Equals(endpoint.Tag, tag, StringComparison.Ordinal));

}
