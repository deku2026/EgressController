using EgressController.SingBox.Api.Models;
using EgressController.SingBox.Configuration;

namespace EgressController.SingBox.Tests;

public sealed class DohHealthProbeTests
{
    [Theory]
    [InlineData(0, 0, EgressDohConfiguration.CloudflareTag)]
    [InlineData(3, 2, EgressDohConfiguration.CloudflareTag)]
    [InlineData(2, 0, EgressDohConfiguration.DnsPodTag)]
    [InlineData(2, 2, EgressDohConfiguration.CloudflareTag)]
    public async Task Both_queries_start_together_and_only_a_complete_round_is_published(int cf, int pod, string tag)
    {
        var cloudflare = new TaskCompletionSource<SingBoxDnsResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dnspod = new TaskCompletionSource<SingBoxDnsResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requested = new List<string>();
        var pending = DohHealthProbe.RunAsync((host, _) =>
        {
            requested.Add(host);
            return host.Contains("cloudflare") ? cloudflare.Task : dnspod.Task;
        }, TestContext.Current.CancellationToken);
        Assert.Equal(2, requested.Count);
        cloudflare.SetResult(new() { Status = cf });
        Assert.False(pending.IsCompleted); // No failure result while the other DoH is pending.
        dnspod.SetResult(new() { Status = pod });
        var probes = await pending;
        var decision = EgressDohConfiguration.Decide(probes, true, DohRoutingDecision.Default);
        Assert.Equal(tag, decision.DnsTag);
    }

    [Fact]
    public async Task An_unresponsive_endpoint_times_out_even_if_it_ignores_cancellation()
    {
        var never = new TaskCompletionSource<SingBoxDnsResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probes = await DohHealthProbe.RunAsync((host, _) => host.Contains("cloudflare")
            ? never.Task : Task.FromResult(new SingBoxDnsResponse { Status = 0 }),
            TestContext.Current.CancellationToken, TimeSpan.FromMilliseconds(25));
        Assert.False(probes[0].IsHealthy);
        Assert.Contains("超时", probes[0].Detail);
        Assert.True(probes[1].IsHealthy);
        Assert.Equal(EgressDohConfiguration.DnsPodTag, EgressDohConfiguration.Decide(probes, true, DohRoutingDecision.Default).DnsTag);
    }

    [Fact]
    public async Task Cancellation_is_not_converted_into_two_network_failures()
    {
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var never = new TaskCompletionSource<SingBoxDnsResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = DohHealthProbe.RunAsync((_, _) => never.Task, canceled.Token);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }
}
