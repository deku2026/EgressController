using System.Net;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace EgressController.Transport.Upstream;

/// <summary>
/// Creates an HTTP client whose every TCP connection is established through the user's
/// loopback SOCKS5 provider. It never consults Windows global proxy settings and never falls back direct.
/// </summary>
public static class Socks5HttpClientFactory
{
    public static HttpClient Create(int port, string host = "127.0.0.1")
    {
        ValidatePort(port);
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("SOCKS5 host is required.", nameof(host));

        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectCallback = (context, cancellationToken)
                => ConnectThroughSocks5Async(context, host, port, cancellationToken),
        };
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("EgressController", "1.0"));
        return client;
    }

    private static async ValueTask<Stream> ConnectThroughSocks5Async(
        SocketsHttpConnectionContext context,
        string socksHost,
        int socksPort,
        CancellationToken cancellationToken)
    {
        var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(socksHost, socksPort, cancellationToken).ConfigureAwait(false);
            NetworkStream stream = tcp.GetStream();
            await Socks5ConnectAsync(
                stream,
                context.DnsEndPoint.Host,
                context.DnsEndPoint.Port,
                cancellationToken).ConfigureAwait(false);
            return stream;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private static async Task Socks5ConnectAsync(
        Stream stream,
        string destinationHost,
        int destinationPort,
        CancellationToken cancellationToken)
    {
        string host = new IdnMapping().GetAscii(destinationHost);
        byte[] hostBytes = Encoding.ASCII.GetBytes(host);
        if (hostBytes.Length is < 1 or > 255)
            throw new InvalidOperationException("SOCKS5 目标域名长度非法。");

        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, cancellationToken).ConfigureAwait(false);
        byte[] greeting = new byte[2];
        await ReadExactlyAsync(stream, greeting, cancellationToken).ConfigureAwait(false);
        if (greeting[0] != 0x05 || greeting[1] != 0x00)
            throw new IOException("上游不是支持无认证的 SOCKS5 服务。");

        byte[] request = new byte[7 + hostBytes.Length];
        request[0] = 0x05;
        request[1] = 0x01; // CONNECT
        request[2] = 0x00;
        request[3] = 0x03; // domain name
        request[4] = (byte)hostBytes.Length;
        hostBytes.CopyTo(request, 5);
        request[^2] = (byte)(destinationPort >> 8);
        request[^1] = (byte)destinationPort;
        await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);

        byte[] head = new byte[4];
        await ReadExactlyAsync(stream, head, cancellationToken).ConfigureAwait(false);
        if (head[0] != 0x05 || head[1] != 0x00)
            throw new IOException($"上游 SOCKS5 CONNECT 失败，reply=0x{head[1]:X2}。");

        int addressLength = head[3] switch
        {
            0x01 => 4,
            0x04 => 16,
            0x03 => (await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false)),
            _ => throw new IOException("上游 SOCKS5 返回了未知地址类型。"),
        };
        if (addressLength <= 0 || addressLength > 255)
            throw new IOException("上游 SOCKS5 返回了非法绑定地址长度。");
        byte[] remainder = new byte[addressLength + 2];
        await ReadExactlyAsync(stream, remainder, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] one = new byte[1];
        await ReadExactlyAsync(stream, one, cancellationToken).ConfigureAwait(false);
        return one[0];
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("SOCKS5 server closed the connection early.");
            offset += read;
        }
    }

    private static void ValidatePort(int port)
    {
        if (port is < 1 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(port));
    }
}
