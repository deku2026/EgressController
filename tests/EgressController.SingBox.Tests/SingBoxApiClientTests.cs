using System.Net;
using System.Net.Http.Headers;
using System.Text;
using EgressController.SingBox.Api;

namespace EgressController.SingBox.Tests;

public sealed class SingBoxApiClientTests
{
    [Fact]
    public async Task Rest_diagnostics_and_control_endpoints_use_bearer_auth_and_expected_routes()
    {
        var handler = new FakeHandler();
        using var httpClient = new HttpClient(handler);
        using var client = new SingBoxApiClient(new Uri("http://127.0.0.1:19090"), "test-secret", httpClient);

        var version = await client.GetVersionAsync(TestContext.Current.CancellationToken);
        var config = await client.GetConfigAsync(TestContext.Current.CancellationToken);
        var rules = await client.GetRulesAsync(TestContext.Current.CancellationToken);
        var connections = await client.GetConnectionsAsync(TestContext.Current.CancellationToken);
        await client.CloseConnectionAsync("connection/one", TestContext.Current.CancellationToken);
        await client.CloseAllConnectionsAsync(TestContext.Current.CancellationToken);
        _ = await client.QueryDnsAsync("example.com", "a", TestContext.Current.CancellationToken);
        await client.FlushDnsCacheAsync(TestContext.Current.CancellationToken);
        await client.FlushFakeIpCacheAsync(TestContext.Current.CancellationToken);

        Assert.Equal("sing-box 1.13.19", version.Version);
        Assert.Equal("Rule", config.Mode);
        Assert.Single(rules.Rules);
        Assert.Equal("conn-1", connections.Connections.Single().Id);
        Assert.Equal(9, connections.UploadTotal);
        Assert.Equal(12, connections.DownloadTotal);
        Assert.All(handler.Requests, request => Assert.Equal("Bearer test-secret", request.Authorization));
        Assert.Equal(
            [
                "GET /version",
                "GET /configs",
                "GET /rules",
                "GET /connections",
                "DELETE /connections/connection%2Fone",
                "DELETE /connections",
                "GET /dns/query?name=example.com&type=A",
                "POST /cache/dns/flush",
                "POST /cache/fakeip/flush",
            ],
            handler.Requests.Select(request => $"{request.Method} {request.PathAndQuery}"));
    }

    [Fact]
    public async Task Invalid_secret_response_is_an_explicit_authentication_failure()
    {
        var handler = new FakeHandler { Unauthorized = true };
        using var httpClient = new HttpClient(handler);
        using var client = new SingBoxApiClient(new Uri("http://127.0.0.1:19090"), "wrong-secret", httpClient);

        SingBoxApiException exception = await Assert.ThrowsAsync<SingBoxApiException>(
            () => client.GetVersionAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Contains("authentication failed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("wrong-secret", exception.Message);
    }

    [Fact]
    public void WebSocket_urls_are_loopback_and_do_not_put_the_secret_in_the_url()
    {
        using var client = new SingBoxApiClient(new Uri("https://[::1]:19090"), "secret-value");

        Uri traffic = client.CreateWebSocketUri("traffic");
        Uri connections = client.CreateWebSocketUri("connections?interval=250");

        Assert.Equal("wss", traffic.Scheme);
        Assert.Equal("[::1]", traffic.Host);
        Assert.Equal("/traffic", traffic.AbsolutePath);
        Assert.Equal("/connections?interval=250", connections.PathAndQuery);
        Assert.DoesNotContain("secret-value", traffic.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oversized_rest_body_is_rejected_before_json_deserialization()
    {
        var handler = new FakeHandler { Oversized = true };
        using var httpClient = new HttpClient(handler);
        using var client = new SingBoxApiClient(new Uri("http://127.0.0.1:19090"), "secret", httpClient);

        SingBoxApiException exception = await Assert.ThrowsAsync<SingBoxApiException>(
            () => client.GetVersionAsync(TestContext.Current.CancellationToken));

        Assert.Contains("bounded size", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Non_loopback_controller_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new SingBoxApiClient(new Uri("http://192.0.2.10:9090"), "secret"));
        Assert.Throws<ArgumentException>(() => new SingBoxApiClient(new Uri("http://127.0.0.1:9090"), " "));
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<ObservedRequest> Requests { get; } = [];
        public bool Unauthorized { get; init; }
        public bool Oversized { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri!;
            Requests.Add(new ObservedRequest(
                request.Method.Method,
                uri.PathAndQuery,
                request.Headers.Authorization?.ToString() ?? string.Empty));

            if (Unauthorized)
                return Task.FromResult(Json(HttpStatusCode.Unauthorized, "{\"error\":\"unauthorized\"}"));
            if (Oversized)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[SingBoxApiClient.MaxRestResponseBytes + 1]),
                });

            string response = uri.AbsolutePath switch
            {
                "/version" => "{\"version\":\"sing-box 1.13.19\",\"premium\":true,\"meta\":true}",
                "/configs" => "{\"mode\":\"Rule\",\"mode-list\":[\"Rule\"],\"log-level\":\"info\"}",
                "/rules" => "{\"rules\":[{\"type\":\"field\",\"payload\":\"example.com\",\"proxy\":\"esim-direct\"}]}",
                "/connections" when request.Method == HttpMethod.Get => "{\"downloadTotal\":12,\"uploadTotal\":9,\"connections\":[{\"id\":\"conn-1\",\"metadata\":{\"network\":\"tcp\",\"type\":\"tun-in\",\"host\":\"example.com\",\"processPath\":\"C:/app.exe\"},\"upload\":3,\"download\":4,\"start\":\"2026-08-23T00:00:00Z\",\"chains\":[\"esim-direct\"],\"rule\":\"example.com => esim-direct\",\"rulePayload\":\"example.com\"}]}",
                "/dns/query" => "{\"Status\":0,\"Question\":[],\"Server\":\"internal\",\"Answer\":[]}",
                _ => "",
            };

            return Task.FromResult(Json(
                request.Method == HttpMethod.Get ? HttpStatusCode.OK : HttpStatusCode.NoContent,
                response));
        }

        private static HttpResponseMessage Json(HttpStatusCode statusCode, string body)
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return response;
        }
    }

    private sealed record ObservedRequest(string Method, string PathAndQuery, string Authorization);
}
