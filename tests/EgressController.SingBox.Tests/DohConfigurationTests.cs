using EgressController.SingBox.Configuration;

namespace EgressController.SingBox.Tests;

public sealed class DohConfigurationTests
{
    [Fact]
    public void Cloudflare_failure_uses_healthy_backup_without_protection()
    {
        DohRoutingDecision decision = EgressDohConfiguration.Decide(
            [
                new DohProbeResult(EgressDohConfiguration.CloudflareTag, false, "timeout"),
                new DohProbeResult(EgressDohConfiguration.DnsPodTag, true),
            ],
            dnsReady: true,
            DohRoutingDecision.Default);

        Assert.Equal(EgressDohConfiguration.DnsPodTag, decision.DnsTag);
    }

    [Fact]
    public void Recovered_cloudflare_becomes_the_default_again()
    {
        DohRoutingDecision decision = EgressDohConfiguration.Decide(
            [
                new DohProbeResult(EgressDohConfiguration.CloudflareTag, true),
                new DohProbeResult(EgressDohConfiguration.DnsPodTag, true),
            ],
            dnsReady: true,
            new DohRoutingDecision { DnsTag = EgressDohConfiguration.DnsPodTag });

        Assert.Equal(EgressDohConfiguration.CloudflareTag, decision.DnsTag);
    }

    [Fact]
    public void Both_failed_keep_the_previously_selected_resolver()
    {
        DohRoutingDecision decision = EgressDohConfiguration.Decide(
            [
                new DohProbeResult(EgressDohConfiguration.CloudflareTag, false),
                new DohProbeResult(EgressDohConfiguration.DnsPodTag, false),
            ],
            dnsReady: true,
            DohRoutingDecision.Default);

        Assert.Equal(EgressDohConfiguration.CloudflareTag, decision.DnsTag);
    }

    [Fact]
    public void Offline_esim_keeps_the_current_selection()
    {
        DohRoutingDecision decision = EgressDohConfiguration.Decide(
            Array.Empty<DohProbeResult>(),
            dnsReady: false,
            DohRoutingDecision.Default);

        Assert.Equal(EgressDohConfiguration.CloudflareTag, decision.DnsTag);
    }

    [Fact]
    public void Probe_host_is_unique_but_stays_inside_the_endpoint_rule_suffix()
    {
        SingBoxDohEndpointDefinition endpoint = EgressDohConfiguration.Endpoints[0];
        string host = endpoint.CreateProbeHost("ABC123");

        Assert.StartsWith("health-abc123.", host, StringComparison.Ordinal);
        Assert.EndsWith(endpoint.ProbeSuffix, host, StringComparison.Ordinal);
    }
}
