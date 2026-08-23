using System.Net;
using System.Net.Sockets;
using System.Text;
using EgressController.Windows.Network;

namespace EgressController.Windows.IntegrationTests;

public sealed class UpstreamSocksProbeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Real_socks5_no_auth_server_is_ready()
    {
        using var listener = StartListener(out int port);
        Task server = RespondToGreetingAsync(listener, [0x05, 0x00]);

        Socks5ProbeResult result = await new UpstreamSocksProbe(TimeSpan.FromSeconds(2)).ProbeAsync(port, Ct);
        await server;

        Assert.True(result.IsReady, result.Message);
        Assert.Equal(Socks5ProbeStatus.Ready, result.Status);
    }

    [Fact]
    public async Task Ordinary_tcp_server_is_not_accepted_as_socks5()
    {
        using var listener = StartListener(out int port);
        Task server = RespondToPlainTcpAsync(listener);

        Socks5ProbeResult result = await new UpstreamSocksProbe(TimeSpan.FromSeconds(2)).ProbeAsync(port, Ct);
        await server;

        Assert.True(result.Status == Socks5ProbeStatus.NotSocks5, result.Message);
        Assert.False(result.IsReady);
    }

    [Fact]
    public async Task Authentication_required_is_reported_without_trying_credentials()
    {
        using var listener = StartListener(out int port);
        Task server = RespondToGreetingAsync(listener, [0x05, 0x02]);

        Socks5ProbeResult result = await new UpstreamSocksProbe(TimeSpan.FromSeconds(2)).ProbeAsync(port, Ct);
        await server;

        Assert.Equal(Socks5ProbeStatus.AuthenticationRequired, result.Status);
        Assert.Contains("凭据", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Closed_port_is_offline()
    {
        using var listener = StartListener(out int port);
        listener.Stop();

        Socks5ProbeResult result = await new UpstreamSocksProbe(TimeSpan.FromMilliseconds(250)).ProbeAsync(port, Ct);

        Assert.Equal(Socks5ProbeStatus.Offline, result.Status);
    }

    private static TcpListener StartListener(out int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return listener;
    }

    private static Task RespondToGreetingAsync(
        TcpListener listener,
        byte[] response)
        => RespondAsync(listener, response);

    private static async Task RespondToPlainTcpAsync(TcpListener listener)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(Ct);
        NetworkStream stream = client.GetStream();
        byte[] greeting = new byte[3];
        await ReadExactlyAsync(stream, greeting, Ct);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("OK"), Ct);
    }

    private static async Task RespondAsync(TcpListener listener, byte[] response)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(Ct);
        NetworkStream stream = client.GetStream();
        byte[] greeting = new byte[3];
        await ReadExactlyAsync(stream, greeting, Ct);
        Assert.Equal([0x05, 0x01, 0x00], greeting);
        await stream.WriteAsync(response, Ct);
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0)
                throw new EndOfStreamException();
            offset += read;
        }
    }
}
