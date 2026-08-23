using System.Net;
using System.Net.Http.Headers;
using EgressController.Core.Contracts;

namespace EgressController.Transport.Upstream;

/// <summary>
/// Control-plane fetcher for the required loopback SOCKS5 upstream. It is deliberately separate
/// from the legacy HTTP-proxy fetcher so the migrated SRS/core path cannot silently fall back to
/// direct Windows networking.
/// </summary>
public sealed class Socks5RemoteFetcher : IRemoteFetcher, IDisposable
{
    private readonly HttpClient _client;
    private bool _disposed;

    public Socks5RemoteFetcher(string host = "127.0.0.1", int port = 7890)
    {
        _client = Socks5HttpClientFactory.Create(port, host);
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

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
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (output.Length + read > maxBytes)
                throw new InvalidDataException($"remote response exceeds {maxBytes} bytes");
            output.Write(buffer, 0, read);
        }
        return new RemoteFetchResult(true, statusCode, output.ToArray());
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _client.Dispose();
    }
}
