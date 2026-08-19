using System.Net.Sockets;
using System.Text;
using EgressController.Core.Contracts;

namespace EgressController.Transport.Upstream;

/// <summary>
/// HTTP-compatible upstream connector (CsWin32-free; managed sockets). Establishes CONNECT
/// tunnels or raw next-hop streams. Bounded timeouts; throws <see cref="UpstreamUnavailableException"/>
/// when the upstream is down / not HTTP (fail-closed, never falls back to direct).
/// </summary>
public sealed class UpstreamHttpProxyConnector : IUpstreamProxyConnector
{
    private readonly string _host;
    private readonly int _port;

    public UpstreamHttpProxyConnector(string host, int port)
    {
        _host = host;
        _port = port;
    }

    public string Endpoint => $"{_host}:{_port}";

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan TunnelResponseTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public async Task<Stream> OpenNextHopAsync(CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ConnectTimeout);
        try
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(_host, _port, cts.Token).ConfigureAwait(false);
            return tcp.GetStream();
        }
        catch (Exception ex)
        {
            throw new UpstreamUnavailableException(Endpoint, ex);
        }
    }

    public async Task<Stream> ConnectTunnelAsync(string host, int port, CancellationToken cancellationToken)
    {
        Stream nextHop;
        try
        {
            nextHop = await OpenNextHopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (UpstreamUnavailableException)
        {
            throw;
        }

        try
        {
            string request = $"CONNECT {FormatAuthority(host, port)} HTTP/1.1\r\nHost: {FormatAuthority(host, port)}\r\n\r\n";
            byte[] requestBytes = Encoding.ASCII.GetBytes(request);
            await nextHop.WriteAsync(requestBytes, cancellationToken).ConfigureAwait(false);
            await nextHop.FlushAsync(cancellationToken).ConfigureAwait(false);

            // Read response head (bounded) until \r\n\r\n.
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readCts.CancelAfter(TunnelResponseTimeout);

            byte[] term = "\r\n\r\n"u8.ToArray();
            var head = new List<byte>(512);
            byte[] one = new byte[1];
            while (true) // bounded by timeout + 32KB cap below
            {
                if (head.Count >= 32 * 1024)
                    throw new UpstreamUnavailableException(Endpoint, new IOException("upstream CONNECT response head too large"));

                // match the last 4 bytes against "\r\n\r\n"
                if (head.Count >= 4)
                {
                    bool found = true;
                    for (int j = 0; j < 4; j++)
                        if (head[head.Count - 4 + j] != term[j]) { found = false; break; }
                    if (found)
                        break;
                }

                int n = await nextHop.ReadAsync(one, readCts.Token).ConfigureAwait(false);
                if (n == 0)
                    throw new UpstreamUnavailableException(Endpoint,
                        new IOException("upstream closed before CONNECT response"));
                head.Add(one[0]);
            }

            string firstLine = Encoding.ASCII.GetString(head.ToArray()).Split('\r')[0];
            if (!Is2xx(firstLine))
            {
                nextHop.Dispose();
                throw new UpstreamUnavailableException(Endpoint,
                    new InvalidOperationException($"upstream refused CONNECT: {firstLine}"));
            }

            return nextHop;
        }
        catch
        {
            nextHop.Dispose();
            throw;
        }
    }

    ValueTask<Stream> IUpstreamProxyConnector.ConnectTunnelAsync(string host, int port, CancellationToken ct)
        => new(ConnectTunnelAsync(host, port, ct));

    ValueTask<Stream> IUpstreamProxyConnector.OpenNextHopAsync(CancellationToken ct)
        => new(OpenNextHopAsync(ct));

    private static bool Is2xx(string statusLine)
    {
        // "HTTP/1.1 200 Connection Established"
        string[] parts = statusLine.Split(' ');
        return parts.Length >= 2
            && (parts[0].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
            && parts[1].Length == 3
            && parts[1][0] == '2'
            && int.TryParse(parts[1], out _);
    }

    private static string FormatAuthority(string host, int port)
        => host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";
}