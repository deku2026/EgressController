using EgressController.SingBox.Configuration;

namespace EgressController.SingBox.Tests;

public sealed class DohConfigurationTests
{
    [Fact]
    public void Only_cloudflare_is_configured_and_checked_once_a_minute()
    {
        var endpoint = Assert.Single(EgressDohConfiguration.Endpoints);
        Assert.Equal("Cloudflare", endpoint.Provider);
        Assert.Equal("cloudflare-dns.com", endpoint.Server);
        Assert.Equal("dns-direct", endpoint.Detour);
        Assert.False(endpoint.IsFallback);
        Assert.Equal(TimeSpan.FromMinutes(1), EgressDohConfiguration.CheckInterval);
    }

    [Fact]
    public void Probe_host_is_unique_but_stays_inside_the_endpoint_rule_suffix()
    {
        var endpoint = Assert.Single(EgressDohConfiguration.Endpoints);
        string host = endpoint.CreateProbeHost("ABC123");
        Assert.StartsWith("health-abc123.", host, StringComparison.Ordinal);
        Assert.EndsWith(endpoint.ProbeSuffix, host, StringComparison.Ordinal);
    }
}
