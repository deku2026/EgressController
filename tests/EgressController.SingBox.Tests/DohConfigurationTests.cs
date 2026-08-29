using EgressController.SingBox.Configuration;

namespace EgressController.SingBox.Tests;

public sealed class DohConfigurationTests
{
    [Fact]
    public void Failed_cloudflare_uses_healthy_google_on_the_same_exit()
    {
        DohRoutingDecision decision = EgressDohConfiguration.Decide(
            [
                new DohProbeResult(EgressDohConfiguration.EsimCloudflareTag, false, "timeout"),
                new DohProbeResult(EgressDohConfiguration.EsimGoogleTag, true),
                new DohProbeResult(EgressDohConfiguration.ClashCloudflareTag, true),
                new DohProbeResult(EgressDohConfiguration.ClashGoogleTag, true),
            ],
            esimReady: true,
            DohRoutingDecision.Default);

        Assert.Equal(EgressDohConfiguration.EsimGoogleTag, decision.EsimDnsTag);
        Assert.Equal(EgressDohConfiguration.ClashCloudflareTag, decision.ClashDnsTag);
        Assert.False(decision.FailClosed);
    }

    [Fact]
    public void Both_failed_exit_planes_enter_fail_closed_mode()
    {
        DohRoutingDecision decision = EgressDohConfiguration.Decide(
            [
                new DohProbeResult(EgressDohConfiguration.EsimCloudflareTag, false),
                new DohProbeResult(EgressDohConfiguration.EsimGoogleTag, false),
                new DohProbeResult(EgressDohConfiguration.ClashCloudflareTag, false),
                new DohProbeResult(EgressDohConfiguration.ClashGoogleTag, false),
            ],
            esimReady: true,
            DohRoutingDecision.Default);

        Assert.True(decision.FailClosed);
        Assert.Equal(EgressDohConfiguration.EsimCloudflareTag, decision.EsimDnsTag);
        Assert.Equal(EgressDohConfiguration.ClashCloudflareTag, decision.ClashDnsTag);
    }

    [Fact]
    public void Offline_esim_does_not_turn_a_healthy_clash_doh_into_failure()
    {
        DohRoutingDecision decision = EgressDohConfiguration.Decide(
            [
                new DohProbeResult(EgressDohConfiguration.ClashCloudflareTag, true),
                new DohProbeResult(EgressDohConfiguration.ClashGoogleTag, true),
            ],
            esimReady: false,
            DohRoutingDecision.Default);

        Assert.False(decision.FailClosed);
        Assert.Equal(EgressDohConfiguration.ClashCloudflareTag, decision.ClashDnsTag);
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
