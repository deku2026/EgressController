using System.Net.Http.Headers;
using EgressController.Core.Contracts;

namespace EgressController.Transport.Upstream;

/// <summary>Bounded artifact/catalog transfer through the explicitly injected download transport.</summary>
public sealed class HttpRemoteFetcher : IRemoteFetcher, IDisposable
{
    private readonly IHttpDownloadTransport _transport;
    private bool _disposed;
    public HttpRemoteFetcher(HttpClient client) : this(new ClientDownloadTransport(client)) { }
    public HttpRemoteFetcher(IHttpDownloadTransport transport) => _transport = transport;
    public async ValueTask<RemoteFetchResult> FetchAsync(Uri uri, int maxBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (uri.Scheme is not ("http" or "https")) throw new ArgumentException("Only HTTP(S) URLs are allowed.", nameof(uri));
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await _transport.ExecuteAsync(uri, async (client, token) =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var output = new MemoryStream(Math.Min(maxBytes, 64 * 1024));
            await HttpTransfer.CopyBoundedAsync(stream, output, maxBytes, token).ConfigureAwait(false);
            return new RemoteFetchResult(true, status, output.ToArray());
        }, cancellationToken).ConfigureAwait(false);
    }
    public void Dispose() => _disposed = true;
}
