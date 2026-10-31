using System.Net;
using EgressController.Runtime;

namespace EgressController.Runtime.Tests;

public sealed class SystemResourceTests
{
    private static ResourceDescriptor Resource(PreparationCachePolicy policy = PreparationCachePolicy.CacheFirst, int maximum = 32) => new()
    {
        Id = "bootstrap", Url = new("https://resource.example.invalid/source?token=synthetic-private"),
        Exit = ResourceExitSnapshot.SystemDefault, CachePolicy = policy, MaximumBytes = maximum
    };

    [Fact]
    public async Task System_proxy_download_needs_no_physical_interface_and_keeps_proxy_on_https_redirects()
    {
        var proxy = new WebProxy("http://127.0.0.1:7890"); var resolver = new Resolver(proxy); var requests = new List<Uri>();
        var transport = new SystemProxyResourceTransport(resolver, sockets =>
        {
            Assert.True(sockets.UseProxy); Assert.Same(proxy, sockets.Proxy); Assert.Null(sockets.ConnectCallback);
            Assert.False(sockets.AllowAutoRedirect); Assert.False(sockets.UseCookies);
            return new Handler((request, _) =>
            {
                requests.Add(request.RequestUri!);
                var response = new HttpResponseMessage(requests.Count == 1 ? HttpStatusCode.Found : HttpStatusCode.OK);
                if (requests.Count == 1) response.Headers.Location = new("https://cdn.example.invalid/final");
                else response.Content = new ByteArrayContent([1, 2, 3]);
                return Task.FromResult(response);
            });
        });
        var prepare = new ResourcePreparation(transport, new InMemoryResourceCache());
        Assert.Equal(new byte[] { 1, 2, 3 }, await prepare.PrepareAsync(Resource(), bytes => bytes.Length == 3, TestContext.Current.CancellationToken));
        Assert.Equal(2, resolver.Calls); Assert.Equal(2, requests.Count);
    }

    [Fact]
    public async Task Proxy_failure_never_retries_direct_and_never_exposes_credentials_or_replaces_cache()
    {
        var resolver = new Resolver(new WebProxy("socks5://127.0.0.1:7890")); int requests = 0;
        var transport = new SystemProxyResourceTransport(resolver, _ => new Handler((_, _) =>
        { requests++; throw new HttpRequestException("private token=synthetic-private; proxy-password=secret"); }));
        var cache = new InMemoryResourceCache(); var descriptor = Resource(PreparationCachePolicy.Refresh);
        await cache.WriteAsync(ResourcePreparation.CacheKey(descriptor), [7], TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<ResourcePreparationException>(() => new ResourcePreparation(transport, cache)
            .PrepareAsync(descriptor, _ => true, TestContext.Current.CancellationToken));
        Assert.Equal(1, requests); Assert.Equal(1, resolver.Calls); Assert.Equal("system-proxy", error.InterfaceId);
        Assert.DoesNotContain("synthetic-private", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(new byte[] { 7 }, await cache.ReadAsync(ResourcePreparation.CacheKey(descriptor), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Valid_cache_bootstraps_without_proxy_resolution_or_another_clients_tun()
    {
        var cache = new InMemoryResourceCache(); var resource = Resource();
        await cache.WriteAsync(ResourcePreparation.CacheKey(resource), [4, 5], TestContext.Current.CancellationToken);
        var resolver = new Resolver(null);
        var transport = new SystemProxyResourceTransport(resolver, _ => throw new InvalidOperationException("Cache should avoid all transport."));
        Assert.Equal(new byte[] { 4, 5 }, await new ResourcePreparation(transport, cache).PrepareAsync(resource, _ => true, TestContext.Current.CancellationToken));
        Assert.Equal(0, resolver.Calls);
    }

    [Theory]
    [InlineData("http://resource.example.invalid/downgrade")]
    [InlineData("https://user:secret@resource.example.invalid/private")]
    public async Task Unsafe_redirects_cannot_escape_the_https_resource_policy(string redirect)
    {
        var resolver = new Resolver(null); int requests = 0;
        var transport = new SystemProxyResourceTransport(resolver, sockets =>
        {
            Assert.False(sockets.UseProxy); Assert.Null(sockets.ConnectCallback);
            return new Handler((_, _) =>
            {
                requests++; var response = new HttpResponseMessage(HttpStatusCode.Found);
                response.Headers.Location = new(redirect); return Task.FromResult(response);
            });
        });
        await Assert.ThrowsAsync<IOException>(() => transport.DownloadAsync(Resource(), TestContext.Current.CancellationToken));
        Assert.Equal(1, requests); Assert.Equal(1, resolver.Calls);
    }

    [Fact]
    public async Task Cancellation_and_size_limits_are_preserved_on_the_system_exit()
    {
        var resolver = new Resolver(null);
        var transport = new SystemProxyResourceTransport(resolver, _ => new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) })));
        await Assert.ThrowsAsync<IOException>(() => transport.DownloadAsync(Resource(maximum: 2), TestContext.Current.CancellationToken));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        int before = resolver.Calls;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.DownloadAsync(Resource(), canceled.Token));
        Assert.Equal(before, resolver.Calls);
    }

    [Fact]
    public async Task Bound_interface_requests_cannot_accidentally_use_the_system_exit()
    {
        var resolver = new Resolver(null); var transport = new SystemProxyResourceTransport(resolver);
        var bound = new ResourceDescriptor { Id = "bound", Url = Resource().Url, Exit = ResourceExitSnapshot.Bound(new("selected", "en7")) };
        await Assert.ThrowsAsync<ArgumentException>(() => transport.DownloadAsync(bound, TestContext.Current.CancellationToken));
        Assert.Equal(0, resolver.Calls);
    }

    private sealed class Resolver(IWebProxy? proxy) : ISystemResourceProxyResolver
    {
        public int Calls { get; private set; }
        public IWebProxy? Resolve(Uri destination) { Calls++; return proxy; }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
}
