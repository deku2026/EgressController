using System.Net;
using System.Net.Http.Headers;
using EgressController.Core.Contracts;

namespace EgressController.Transport.Upstream;

/// <summary>
/// Bounded control-plane HTTP(S) fetcher that uses only the explicitly configured upstream
/// proxy. It never consults the Windows System Proxy, so rule updates cannot recurse into the
/// local router at 127.0.0.1:18080.
/// </summary>
public sealed class UpstreamRemoteFetcher : IRemoteFetcher, IDisposable
{
    private readonly HttpClient _client;
    private bool _disposed;

    public UpstreamRemoteFetcher(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("Upstream host is required.", nameof(host));
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));

        var handler = new SocketsHttpHandler
        {
            // An explicit proxy is intentional. SocketsHttpHandler does not fall back to a
            // direct connection when this proxy is unavailable.
            Proxy = new WebProxy($"http://{FormatHost(host)}:{port}"),
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };

        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("EgressController", "1.0"));
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
        byte[] body = await ReadBoundedAsync(stream, maxBytes, cancellationToken).ConfigureAwait(false);
        return new RemoteFetchResult(true, statusCode, body);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _client.Dispose();
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
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
        return output.ToArray();
    }

    private static string FormatHost(string host)
        => host.Contains(':') && !host.StartsWith("[", StringComparison.Ordinal)
            ? $"[{host}]"
            : host;
}
