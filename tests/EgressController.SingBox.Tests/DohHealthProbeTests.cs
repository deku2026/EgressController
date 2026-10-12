using EgressController.SingBox.Api.Models;
using EgressController.SingBox.Configuration;

namespace EgressController.SingBox.Tests;

public sealed class DohHealthProbeTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(3, true)]
    [InlineData(2, false)]
    [InlineData(5, false)]
    [InlineData(-1, false)]
    public async Task Only_cloudflare_is_queried_and_a_completed_response_decides_health(int status, bool healthy)
    {
        var completion = new TaskCompletionSource<SingBoxDnsResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requested = new List<string>();
        var pending = DohHealthProbe.RunAsync((host, _) =>
        {
            requested.Add(host);
            return completion.Task;
        }, TestContext.Current.CancellationToken);
        Assert.EndsWith(".doh-global-cloudflare.egresscontroller.invalid", Assert.Single(requested));
        Assert.False(pending.IsCompleted);
        completion.SetResult(new() { Status = status });
        var probe = Assert.Single(await pending);
        Assert.Equal(EgressDohConfiguration.CloudflareTag, probe.Tag);
        Assert.Equal(healthy, probe.IsHealthy);
    }

    [Fact]
    public async Task Each_check_uses_a_new_uncached_hostname()
    {
        var requested = new List<string>();
        Task<SingBoxDnsResponse> Query(string host, CancellationToken _)
        {
            requested.Add(host);
            return Task.FromResult(new SingBoxDnsResponse { Status = 3 });
        }
        await DohHealthProbe.RunAsync(Query, TestContext.Current.CancellationToken);
        await DohHealthProbe.RunAsync(Query, TestContext.Current.CancellationToken);
        Assert.Equal(2, requested.Distinct().Count());
    }

    [Fact]
    public async Task Unresponsive_cloudflare_times_out_even_if_it_ignores_cancellation()
    {
        var never = new TaskCompletionSource<SingBoxDnsResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = Assert.Single(await DohHealthProbe.RunAsync((_, _) => never.Task,
            TestContext.Current.CancellationToken, TimeSpan.FromMilliseconds(25)));
        Assert.False(probe.IsHealthy);
        Assert.Contains("超时", probe.Detail);
    }

    [Fact]
    public async Task Transport_errors_are_failed_results()
    {
        var probe = Assert.Single(await DohHealthProbe.RunAsync((_, _) => throw new HttpRequestException("offline"),
            TestContext.Current.CancellationToken));
        Assert.False(probe.IsHealthy);
        Assert.Equal("offline", probe.Detail);
    }

    [Fact]
    public async Task Cancellation_is_not_converted_into_a_network_failure()
    {
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var never = new TaskCompletionSource<SingBoxDnsResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = DohHealthProbe.RunAsync((_, _) => never.Task, canceled.Token);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }
}
