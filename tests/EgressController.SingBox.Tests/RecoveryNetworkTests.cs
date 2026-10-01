using System.Net;
using System.Text;
using EgressController.SingBox.Api;
using EgressController.Transport.Upstream;

namespace EgressController.SingBox.Tests;

public sealed class RecoveryNetworkTests
{
    [Fact]
    public async Task Outbound_probe_uses_the_named_route_and_an_authenticated_https_probe()
    {
        using var handler = new StubHandler("{\"delay\":12}");
        using var http = new HttpClient(handler);
        using var api = new SingBoxApiClient(new("http://127.0.0.1:19090"), "test-secret", http);
        var result = await api.ProbeOutboundAsync("adapter-test", TestContext.Current.CancellationToken);
        Assert.Equal(12, result.Delay);
        Assert.StartsWith("/proxies/adapter-test/delay?timeout=5000&url=https", handler.Path);
        Assert.Equal("Bearer test-secret", handler.Authorization);
    }

    [Fact]
    public async Task Failed_outbound_probe_is_not_reported_as_healthy()
    {
        using var http = new HttpClient(new StubHandler("{\"error\":\"timeout\"}") { Status = HttpStatusCode.GatewayTimeout });
        using var api = new SingBoxApiClient(new("http://127.0.0.1:19090"), "test-secret", http);
        await Assert.ThrowsAsync<SingBoxApiException>(() => api.ProbeOutboundAsync("clash-7890", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Recovery_download_uses_its_supplied_transport_without_requiring_a_socks_listener()
    {
        using var http = new HttpClient(new StubHandler("core-bytes"));
        using var fetcher = new HttpRemoteFetcher(http);
        var result = await fetcher.FetchAsync(new("https://example.invalid/core.zip"), 1024, TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fetcher.FetchAsync(new("https://example.invalid/core.zip"), 2, TestContext.Current.CancellationToken));
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public string Path { get; private set; } = "";
        public string Authorization { get; private set; } = "";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Path = request.RequestUri!.PathAndQuery;
            Authorization = request.Headers.Authorization?.ToString() ?? "";
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
