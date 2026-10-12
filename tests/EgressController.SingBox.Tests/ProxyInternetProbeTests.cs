using System.Net;
using EgressController.SingBox.Api;
using EgressController.SingBox.Api.Models;
using EgressController.SingBox.Configuration;

namespace EgressController.SingBox.Tests;

public sealed class ProxyInternetProbeTests
{
    [Theory]
    [InlineData("{\"delay\":12}", HttpStatusCode.OK, true)]
    [InlineData("{\"delay\":0}", HttpStatusCode.OK, true)]
    [InlineData("{\"delay\":-1}", HttpStatusCode.OK, false)]
    [InlineData("{}", HttpStatusCode.OK, false)]
    [InlineData("invalid json", HttpStatusCode.OK, false)]
    [InlineData("{\"error\":\"offline\"}", HttpStatusCode.GatewayTimeout, false)]
    public async Task Http_probe_uses_only_bound_proxy_direct_and_validates_the_response(string body, HttpStatusCode status, bool healthy)
    {
        using var handler = new FakeHandler(body, status);
        using var http = new HttpClient(handler);
        using var api = new SingBoxApiClient(new("http://127.0.0.1:19090"), "mock-secret", http);
        var result = await ProxyInternetProbe.RunAsync(api.ProbeOutboundAsync, TestContext.Current.CancellationToken);
        Assert.Equal(healthy, result.IsHealthy);
        Assert.StartsWith("/proxies/proxy-direct/delay?", handler.Path);
        Assert.Contains("https://www.cloudflare.com/cdn-cgi/trace", Uri.UnescapeDataString(handler.Path));
        Assert.Equal("Bearer mock-secret", handler.Authorization);
    }

    [Fact]
    public async Task A_hung_network_check_is_bounded_even_if_transport_ignores_cancellation()
    {
        var never = new TaskCompletionSource<SingBoxDelayResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = await ProxyInternetProbe.RunAsync((_, _) => never.Task,
            TestContext.Current.CancellationToken, TimeSpan.FromMilliseconds(25));
        Assert.False(result.IsHealthy);
        Assert.Contains("超时", result.Detail);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_an_outage()
    {
        using var canceled = new CancellationTokenSource();
        var never = new TaskCompletionSource<SingBoxDelayResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = ProxyInternetProbe.RunAsync((_, _) => never.Task, canceled.Token);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Dns_response_without_a_status_cannot_pass_the_health_check()
    {
        using var http = new HttpClient(new FakeHandler("{}", HttpStatusCode.OK));
        using var api = new SingBoxApiClient(new("http://127.0.0.1:19090"), "mock-secret", http);
        var result = Assert.Single(await DohHealthProbe.RunAsync((host, ct) => api.QueryDnsAsync(host, "A", ct), TestContext.Current.CancellationToken));
        Assert.False(result.IsHealthy);
    }

    private sealed class FakeHandler(string body, HttpStatusCode status) : HttpMessageHandler
    {
        public string Path { get; private set; } = "";
        public string Authorization { get; private set; } = "";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Path = request.RequestUri!.PathAndQuery;
            Authorization = request.Headers.Authorization?.ToString() ?? "";
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
