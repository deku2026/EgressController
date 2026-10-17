using System.Net;
using System.Net.Http.Headers;
using EgressController.Core.Contracts;

namespace EgressController.Transport.Upstream;

/// <summary>
/// Bounded recovery downloads using the explicitly supplied control-plane client.
/// </summary>
public sealed class HttpRemoteFetcher : IRemoteFetcher, IDisposable
{
    private readonly HttpClient _client;
    private bool _disposed;

    public HttpRemoteFetcher(HttpClient client) => _client = client;

    public async ValueTask<RemoteFetchResult> FetchAsync(
        Uri uri,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Only HTTP(S) URLs are allowed.", nameof(uri));
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var deadline = HttpTransfer.Deadline(_client, cancellationToken);
        cancellationToken = deadline.Token;

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using HttpResponseMessage response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        int statusCode = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode)
            return new RemoteFetchResult(false, statusCode, null);

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream(Math.Min(maxBytes, 64 * 1024));
        await HttpTransfer.CopyBoundedAsync(stream, output, maxBytes, cancellationToken).ConfigureAwait(false);
        return new RemoteFetchResult(true, statusCode, output.ToArray());
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // The controller owns the shared download client.
    }
}
