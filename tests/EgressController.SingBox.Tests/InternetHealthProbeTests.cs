using System.Net;
using EgressController.Core.Protection;
using EgressController.SingBox.Api;
using EgressController.SingBox.Api.Models;
using EgressController.SingBox.Configuration;

namespace EgressController.SingBox.Tests;

public sealed class InternetHealthProbeTests
{
    [Theory]
    [InlineData("{\"delay\":12}", HttpStatusCode.OK, true)]
    [InlineData("{\"delay\":0}", HttpStatusCode.OK, true)]
    [InlineData("{\"delay\":-1}", HttpStatusCode.OK, false)]
    [InlineData("{}", HttpStatusCode.OK, false)]
    [InlineData("invalid json", HttpStatusCode.OK, false)]
    [InlineData("{\"error\":\"offline\"}", HttpStatusCode.GatewayTimeout, false)]
    public async Task Both_adapters_probe_generate_204_via_their_bound_outbound_without_dns_queries(
        string body, HttpStatusCode status, bool healthy)
    {
        foreach (var (check, outbound) in new[]
        {
            (NetworkHealthCheck.DirectInternet, "dns-direct"),
            (NetworkHealthCheck.ProxyInternet, "proxy-direct"),
        })
        {
            using var handler = new FakeHandler(body, status);
            using var http = new HttpClient(handler);
            using var api = new SingBoxApiClient(new("http://127.0.0.1:19090"), "mock-secret", http);
            var result = await InternetHealthProbe.RunAsync(check, api.ProbeOutboundAsync, TestContext.Current.CancellationToken);
            Assert.Equal(healthy, result.IsHealthy);
            Assert.Equal($"/proxies/{outbound}/delay?timeout=5000&url=https://cp.cloudflare.com/generate_204",
                Uri.UnescapeDataString(handler.Path));
            Assert.Equal("Bearer mock-secret", handler.Authorization);
            Assert.Equal(1, handler.RequestCount);
        }
    }

    [Fact]
    public void Both_roles_share_a_one_minute_interval_and_bounded_timeout()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), InternetHealthProbe.CheckInterval);
        Assert.Equal(TimeSpan.FromSeconds(8), InternetHealthProbe.Timeout);
    }

    [Theory]
    [InlineData(NetworkHealthCheck.DirectInternet)]
    [InlineData(NetworkHealthCheck.ProxyInternet)]
    public async Task Hung_checks_are_bounded_even_if_the_transport_ignores_cancellation(NetworkHealthCheck check)
    {
        var never = new TaskCompletionSource<SingBoxDelayResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = await InternetHealthProbe.RunAsync(check, (_, _) => never.Task,
            TestContext.Current.CancellationToken, TimeSpan.FromMilliseconds(25));
        Assert.False(result.IsHealthy);
        Assert.Contains("超时", result.Detail);
    }

    [Theory]
    [InlineData(NetworkHealthCheck.DirectInternet)]
    [InlineData(NetworkHealthCheck.ProxyInternet)]
    public async Task Caller_cancellation_is_not_an_outage(NetworkHealthCheck check)
    {
        using var canceled = new CancellationTokenSource();
        var never = new TaskCompletionSource<SingBoxDelayResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = InternetHealthProbe.RunAsync(check, (_, _) => never.Task, canceled.Token);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private sealed class FakeHandler(string body, HttpStatusCode status) : HttpMessageHandler
    {
        public string Path { get; private set; } = "";
        public string Authorization { get; private set; } = "";
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            RequestCount++;
            Path = request.RequestUri!.PathAndQuery;
            Authorization = request.Headers.Authorization?.ToString() ?? "";
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
