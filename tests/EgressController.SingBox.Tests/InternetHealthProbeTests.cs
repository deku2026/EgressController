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
    public async Task Website_checks_use_residential_direct_and_default_proxy_without_dns_queries(
        string body, HttpStatusCode status, bool healthy)
    {
        foreach (var (check, outbound) in new[]
        {
            (NetworkHealthCheck.DirectInternet, "dns-direct"),
            (NetworkHealthCheck.ProxyInternet, "upstream-1080"),
        })
        {
            using var handler = new FakeHandler(body, status);
            using var http = new HttpClient(handler);
            using var api = new SingBoxApiClient(new("http://127.0.0.1:19090"), "mock-secret", http);
            var result = await InternetHealthProbe.RunAsync(check, 1080, api.ProbeOutboundAsync, TestContext.Current.CancellationToken);
            Assert.Equal(healthy, result.IsHealthy);
            Assert.Equal($"/proxies/{outbound}/delay?timeout=5000&url=https://cp.cloudflare.com/generate_204",
                Uri.UnescapeDataString(handler.Path));
            Assert.Equal("Bearer mock-secret", handler.Authorization);
            Assert.Equal(1, handler.RequestCount);
        }
    }

    [Theory]
    [InlineData(7890)]
    [InlineData(7891)]
    [InlineData(1080)]
    [InlineData(65535)]
    public async Task Proxy_check_uses_the_configured_default_port_without_direct_fallback(int port)
    {
        using var handler = new FakeHandler("{\"message\":\"Timeout\"}", HttpStatusCode.GatewayTimeout);
        using var http = new HttpClient(handler);
        using var api = new SingBoxApiClient(new("http://127.0.0.1:19090"), "mock-secret", http);
        var result = await InternetHealthProbe.RunAsync(NetworkHealthCheck.ProxyInternet, port,
            api.ProbeOutboundAsync, TestContext.Current.CancellationToken);
        Assert.False(result.IsHealthy);
        Assert.StartsWith($"/proxies/upstream-{port}/delay?", handler.Path);
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public async Task Missing_or_invalid_default_proxy_does_not_probe_a_hardcoded_or_direct_exit(int? port)
    {
        var result = await InternetHealthProbe.RunAsync(NetworkHealthCheck.ProxyInternet, port,
            (_, _) => throw new InvalidOperationException("No network request is allowed"), TestContext.Current.CancellationToken);
        Assert.False(result.IsHealthy);
        Assert.Contains("默认代理端口", result.Detail);
    }

    [Fact]
    public async Task Residential_check_is_independent_of_whether_a_proxy_port_is_configured()
    {
        var result = await InternetHealthProbe.RunAsync(NetworkHealthCheck.DirectInternet, null, (outbound, _) =>
        {
            Assert.Equal("dns-direct", outbound);
            return Task.FromResult(new SingBoxDelayResponse { Delay = 10 });
        }, TestContext.Current.CancellationToken);
        Assert.True(result.IsHealthy);
    }

    [Fact]
    public void Both_roles_share_a_thirty_second_interval_and_bounded_timeout()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), InternetHealthProbe.CheckInterval);
        Assert.Equal(TimeSpan.FromSeconds(5), InternetHealthProbe.RetryInterval);
        Assert.Equal(3, InternetHealthProbe.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(8), InternetHealthProbe.Timeout);
    }

    [Theory]
    [InlineData(NetworkHealthCheck.DirectInternet)]
    [InlineData(NetworkHealthCheck.ProxyInternet)]
    public async Task Hung_checks_are_bounded_even_if_the_transport_ignores_cancellation(NetworkHealthCheck check)
    {
        var never = new TaskCompletionSource<SingBoxDelayResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = await InternetHealthProbe.RunAsync(check, 1080, (_, _) => never.Task,
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
        var pending = InternetHealthProbe.RunAsync(check, 1080, (_, _) => never.Task, canceled.Token);
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
