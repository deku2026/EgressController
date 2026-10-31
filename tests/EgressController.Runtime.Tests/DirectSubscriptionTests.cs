using System.Net;
using System.Net.Sockets;
using System.Text;
using EgressController.Policy;
using EgressController.Runtime;

namespace EgressController.Runtime.Tests;

public sealed class DirectSubscriptionTests
{
    private static readonly InterfaceBinding Binding = new("selected-nonhome", "en5");
    private static readonly SubscriptionSource Source = new() { Id = "first", Url = "https://subscription.example.invalid/list?token=synthetic-private" };
    private const string Nodes = "proxies: [{name: Direct node, type: socks5, server: node.example.invalid, port: 1080}]";

    [Fact]
    public async Task FirstSubscriptionProvidersAndRedirectsAreDirectWithoutCoreOrHelper()
    {
        var requests = new List<string>(); var progress = new List<PreparationProgress>();
        var service = new DirectSubscriptionResources(new RejectingBinder(), new Resolver(), new InMemoryResourceCache(), Binding,
            new ProgressSink(progress), sockets =>
            {
                Assert.False(sockets.UseProxy); Assert.Null(sockets.Proxy); Assert.False(sockets.AllowAutoRedirect);
                Assert.False(sockets.UseCookies); Assert.NotNull(sockets.ConnectCallback);
                return new Handler((request, _) =>
                {
                    requests.Add(request.RequestUri!.Host);
                    return Task.FromResult(request.RequestUri.Host switch
                    {
                        "subscription.example.invalid" => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new("https://cdn.example.invalid/subscription") } },
                        "cdn.example.invalid" => Response("proxy-providers: {upstream: {type: http, url: https://provider.example.invalid/list}}\nproxy-groups: [{name: Global, type: select, use: [upstream]}]"),
                        "provider.example.invalid" => Response(Nodes),
                        _ => throw new InvalidOperationException("Unexpected endpoint")
                    });
                });
            });
        var result = await service.FetchAsync(Source, TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess); Assert.Single(result.Nodes); Assert.Single(result.Groups); Assert.Single(result.Providers);
        Assert.Equal(new[] { "subscription.example.invalid", "cdn.example.invalid", "provider.example.invalid" }, requests);
        Assert.Contains(progress, item => item.Stage == PreparationStage.Downloading && item.Message.Contains("直连", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http")]
    [InlineData("tls")]
    [InlineData("timeout")]
    [InlineData("invalid-yaml")]
    public async Task DirectFailureIsActionableRedactedAndCannotReplaceValidatedCache(string failure)
    {
        int requests = 0; var cache = new InMemoryResourceCache();
        var descriptor = new ResourceDescriptor { Id = "subscription-first", Url = new(Source.Url), Exit = ResourceExitSnapshot.DirectSubscription(Binding) };
        byte[] previous = Encoding.UTF8.GetBytes(Nodes);
        await cache.WriteAsync(ResourcePreparation.CacheKey(descriptor), previous, TestContext.Current.CancellationToken);
        var service = new DirectSubscriptionResources(new RejectingBinder(), new Resolver(), cache, Binding, httpHandlerFactory: _ => new Handler((_, _) =>
        {
            requests++;
            return failure switch
            {
                "http" => throw new HttpRequestException("synthetic-private"),
                "tls" => throw new System.Security.Authentication.AuthenticationException("synthetic-private"),
                "timeout" => throw new OperationCanceledException(),
                _ => Task.FromResult(Response("proxies: [broken"))
            };
        }));
        var error = await Assert.ThrowsAsync<ResourcePreparationException>(() => service.FetchAsync(Source, TestContext.Current.CancellationToken));
        Assert.Equal(1, requests); Assert.StartsWith("subscription-direct:", error.InterfaceId);
        Assert.Contains("订阅使用非家宽物理网卡直连", error.Message);
        Assert.Equal(failure == "timeout", error.IsTimeout); Assert.DoesNotContain("synthetic-private", error.ToString());
        Assert.Equal(previous, await cache.ReadAsync(ResourcePreparation.CacheKey(descriptor), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("http://redirect.example.invalid/body")]
    [InlineData("https://user:password@redirect.example.invalid/body")]
    public async Task DirectRedirectNeverDowngradesOrAddsCredentials(string location)
    {
        int requests = 0;
        var service = new DirectSubscriptionResources(new RejectingBinder(), new Resolver(), new InMemoryResourceCache(), Binding, httpHandlerFactory: _ => new Handler((_, _) =>
        { requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new(location) } }); }));
        await Assert.ThrowsAsync<ResourcePreparationException>(() => service.FetchAsync(Source, TestContext.Current.CancellationToken));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task CancelBeforeRequestDoesNotCreateAClient()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var service = new DirectSubscriptionResources(new RejectingBinder(), new Resolver(), new InMemoryResourceCache(), Binding,
            httpHandlerFactory: _ => throw new InvalidOperationException("Canceled subscription created a client."));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.FetchAsync(Source, cancellation.Token));
    }

    [Fact]
    public async Task DirectSocketMustBindBeforeConnectingAndCannotRetryUnbound()
    {
        var binder = new RejectingBinder(); var resolver = new Resolver();
        var transport = new BoundHttpsResourceTransport(binder, resolver);
        await Assert.ThrowsAsync<IOException>(async () => await transport.ConnectAsync(new("subscription.example.invalid", 443), Binding, TestContext.Current.CancellationToken));
        Assert.Equal(Binding, Assert.Single(binder.Bindings)); Assert.Equal(Binding, Assert.Single(resolver.Bindings));
    }

    private static HttpResponseMessage Response(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
    private sealed class RejectingBinder : IPhysicalSocketBinder
    {
        public List<InterfaceBinding> Bindings { get; } = [];
        public void Bind(Socket socket, InterfaceBinding binding) { Bindings.Add(binding); throw new IOException("Synthetic bind refusal before any socket connect."); }
    }
    private sealed class Resolver : IInterfaceDnsResolver
    {
        public List<InterfaceBinding> Bindings { get; } = [];
        public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, InterfaceBinding binding, CancellationToken token)
        { Bindings.Add(binding); return ValueTask.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Loopback]); }
    }
    private sealed class ProgressSink(List<PreparationProgress> items) : IProgress<PreparationProgress>
    { public void Report(PreparationProgress value) => items.Add(value); }
}
