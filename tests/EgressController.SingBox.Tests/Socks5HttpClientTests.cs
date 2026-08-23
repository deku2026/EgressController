using System.Net;
using System.Net.Sockets;
using System.Text;
using EgressController.Transport.Upstream;

namespace EgressController.SingBox.Tests;

public sealed class Socks5HttpClientTests
{
    [Fact]
    public async Task Http_client_sends_the_request_through_the_configured_socks5_port()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task server = Task.Run(async () =>
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            NetworkStream stream = client.GetStream();
            Assert.Equal([0x05, 0x01, 0x00], await ReadExactlyAsync(stream, 3));
            await stream.WriteAsync(new byte[] { 0x05, 0x00 }, TestContext.Current.CancellationToken);

            byte[] head = await ReadExactlyAsync(stream, 5);
            Assert.Equal([0x05, 0x01, 0x00, 0x03], head[..4]);
            int hostLength = head[4];
            byte[] hostAndPort = await ReadExactlyAsync(stream, hostLength + 2);
            Assert.Equal("example.test", Encoding.ASCII.GetString(hostAndPort, 0, hostLength));
            Assert.Equal(80, (hostAndPort[^2] << 8) | hostAndPort[^1]);
            await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 127, 0, 0, 1, 0, 80 }, TestContext.Current.CancellationToken);

            string request = Encoding.ASCII.GetString(await ReadUntilHeaderEndAsync(stream));
            Assert.Contains("GET / HTTP/1.1", request, StringComparison.Ordinal);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"), TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        using HttpClient clientThroughSocks = Socks5HttpClientFactory.Create(port);
        string body = await clientThroughSocks.GetStringAsync("http://example.test/", TestContext.Current.CancellationToken);

        await server;
        Assert.Equal("ok", body);
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count)
    {
        byte[] result = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = await stream.ReadAsync(result.AsMemory(offset, count - offset), TestContext.Current.CancellationToken);
            if (read == 0)
                throw new EndOfStreamException();
            offset += read;
        }
        return result;
    }

    private static async Task<byte[]> ReadUntilHeaderEndAsync(Stream stream)
    {
        using var output = new MemoryStream();
        byte[] one = new byte[1];
        while (output.Length < 32 * 1024)
        {
            byte[] current = await ReadExactlyAsync(stream, 1);
            output.WriteByte(current[0]);
            if (output.Length >= 4)
            {
                byte[] bytes = output.ToArray();
                int length = bytes.Length;
                if (bytes[length - 4] == '\r' && bytes[length - 3] == '\n'
                    && bytes[length - 2] == '\r' && bytes[length - 1] == '\n')
                    return bytes;
            }
        }
        throw new InvalidDataException("HTTP request head too large.");
    }
}
