using EgressController.Core.Contracts;

namespace EgressController.Transport.Upstream;

/// <summary>Fixed-port compatibility adapter; shares the bounded proxy-only download implementation.</summary>
public sealed class Socks5RemoteFetcher : IRemoteFetcher, IDisposable
{
    private readonly HttpRemoteFetcher _inner;
    public Socks5RemoteFetcher(string host = "127.0.0.1", int port = 7890)
        => _inner = new(new DefaultProxyDownloadTransport(() => port, value => Socks5HttpClientFactory.Create(value, host)));
    public ValueTask<RemoteFetchResult> FetchAsync(Uri uri, int maxBytes, CancellationToken cancellationToken = default)
        => _inner.FetchAsync(uri, maxBytes, cancellationToken);
    public void Dispose() => _inner.Dispose();
}
