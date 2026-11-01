using System.Net;
using System.Net.Sockets;
using EgressController.Policy;
using EgressController.Runtime;

namespace EgressController.Runtime.Tests;

public sealed class NetworkRecoveryTests
{
    private static readonly InterfaceBinding Home = new("home-id", "en8");
    [Theory]
    [InlineData(false, "172.20.10.3", "interface.inactive")]
    [InlineData(true, "", "interface.no-address")]
    [InlineData(true, "fe80::1234%8", "interface.no-address")]
    [InlineData(true, "169.254.1.2", "interface.no-address")]
    [InlineData(true, "127.0.0.1,::,ff02::1,255.255.255.255,invalid", "interface.no-address")]
    [InlineData(true, "fe80::1234%8,172.20.10.3", null)]
    [InlineData(true, "2001:db8::1234", null)]
    public void SetupPreflightDistinguishesAttachedFromAddressedInterfaces(bool active, string addresses, string? failure)
        => Assert.Equal(failure, InterfaceConnectivity.ReadinessFailure(active, addresses.Split(',')));

    [Theory]
    [InlineData(SocketError.NetworkUnreachable, "network.unreachable")]
    [InlineData(SocketError.AddressNotAvailable, "network.unreachable")]
    [InlineData(SocketError.TimedOut, "timeout")]
    [InlineData(SocketError.ConnectionRefused, "connection.refused")]
    public void SocketCauseSurvivesDnsAndHttpWrappers(SocketError error, string failure)
        => Assert.Equal(failure, NetworkFailure.Code(new IOException("Bound DNS queries failed", new AggregateException(new HttpRequestException("DoH request failed", new SocketException((int)error))))));

    [Fact]
    public void DnsStackFrameDoesNotChangeClassification()
    {
        try { BoundDnsFixture(); }
        catch (IOException error) { Assert.Equal("connection.failed", NetworkFailure.Code(error)); return; }
        Assert.Fail("Fixture did not throw.");
        static void BoundDnsFixture() => throw new IOException("All connections through the selected physical interface failed.");
    }
    [Theory]
    [InlineData(204, 1, true)]
    [InlineData(200, 2, false)]
    [InlineData(302, 2, false)]
    [InlineData(503, 2, false)]
    public async Task HomeCheckRequiresExactly204WithNoRedirectOrProxy(int status, int count, bool success)
    {
        var requests = new List<string>();
        var checker = new InterfaceConnectivity(new RejectingBinder(), new RejectingResolver(), handler =>
        {
            Assert.False(handler.UseProxy); Assert.False(handler.AllowAutoRedirect); Assert.False(handler.UseCookies);
            Assert.NotNull(handler.ConnectCallback);
            return new HttpStub((request, _) => { requests.Add(request.RequestUri!.Host); return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)); });
        });
        var result = await checker.CheckAsync(Home, TestContext.Current.CancellationToken);
        Assert.Equal(success, result.Available); Assert.Equal(Home.StableId, result.InterfaceId); Assert.Equal(Home.BsdName, result.BsdName);
        Assert.Equal(count, requests.Count); Assert.Equal("cp.cloudflare.com", requests[0]);
        if (count == 2) Assert.Equal("www.gstatic.com", requests[1]);
        if (!success) Assert.Equal("http." + status, result.FailureCode);
    }
    [Fact]
    public async Task CancelingHomeCheckNeverTriesAnotherExitOrEndpoint()
    {
        using var cancel = new CancellationTokenSource(); int calls = 0;
        var checker = new InterfaceConnectivity(new RejectingBinder(), new RejectingResolver(), _ => new HttpStub((_, token) =>
        { calls++; cancel.Cancel(); token.ThrowIfCancellationRequested(); throw new InvalidOperationException(); }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checker.CheckAsync(Home, cancel.Token)); Assert.Equal(1, calls);
    }
    [Fact]
    public async Task EncryptedDnsBindsBothQueriesBeforeConnectAndNeverFallsBackToSystemDns()
    {
        var binder = new RejectingBinder(); int requests = 0;
        var resolver = new BoundDohResolver(binder, "https://1.1.1.1/dns-query", handler =>
        {
            Assert.False(handler.UseProxy); Assert.False(handler.AllowAutoRedirect);
            return new HttpStub(async (request, token) =>
            {
                Interlocked.Increment(ref requests); Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("application/dns-message", request.Content!.Headers.ContentType!.MediaType);
                await handler.ConnectCallback!(null!, token);
                throw new InvalidOperationException("The binder must prevent a real connection.");
            });
        });
        await Assert.ThrowsAsync<IOException>(async () => await resolver.ResolveAsync("example.invalid", Home, TestContext.Current.CancellationToken));
        Assert.Equal(2, requests); Assert.Equal(2, binder.Bindings.Count); Assert.All(binder.Bindings, item => Assert.Equal(Home, item));
    }
    [Theory]
    [InlineData("failed to bind socket IP_BOUND_IF", "interface.bind")]
    [InlineData("failed TLS handshake: certificate rejected", "tls.failed")]
    [InlineData("DNS query failed", "dns.failed")]
    [InlineData("dial: network is unreachable", "network.unreachable")]
    [InlineData("connect: connection refused", "connection.refused")]
    [InlineData("context deadline exceeded", "timeout")]
    [InlineData("All connections through the selected physical interface failed.", "connection.failed")]
    public void FailureClassificationDoesNotMistakeEveryInterfaceMentionForABindFailure(string text, string code)
        => Assert.Equal(code, NetworkFailure.Classify(text));
    [Fact]
    public void NetworkDiagnosticsAreBoundedRedactedAndCannotAffectProtection()
    {
        var log = new BoundedLifecycleLog(); var now = DateTimeOffset.UtcNow;
        var diagnostics = new CoreNetworkDiagnostics(log, () => now);
        for (int i = 0; i < 1000; i++) diagnostics.Observe("[Warning] TLS handshake failed password=private-token https://private.example/secret");
        Assert.Single(log.Events); Assert.DoesNotContain("private", log.Events[0].Cause);
        now = now.AddSeconds(30); diagnostics.Observe("[Warning] TLS handshake failed");
        Assert.Equal(2, log.Events.Count); Assert.Contains("occurrences=1000", log.Events[1].Cause);
        new CoreNetworkDiagnostics(new BrokenLog()).Observe("[Warning] failed to bind");
    }
    private sealed class BrokenLog : ILifecycleLog { public void Append(LifecycleEvent entry) => throw new IOException("Fixture disk failure"); }
    private sealed class RejectingBinder : IPhysicalSocketBinder
    {
        public List<InterfaceBinding> Bindings { get; } = [];
        public void Bind(Socket socket, InterfaceBinding binding) { lock (Bindings) Bindings.Add(binding); throw new IOException("Failed to bind fixture socket"); }
    }
    private sealed class RejectingResolver : IInterfaceDnsResolver
    {
        public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, InterfaceBinding binding, CancellationToken cancellationToken) => throw new InvalidOperationException("No real DNS in fixture");
    }
    private sealed class HttpStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
}
