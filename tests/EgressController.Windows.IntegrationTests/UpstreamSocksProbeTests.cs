using System.Net;
using System.Net.Sockets;
using System.Text;
using EgressController.App.Services;
using EgressController.Windows.Network;
using EgressController.Windows.Process;

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
        Task server = Task.Run(async () =>
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(Ct);
            await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("OK"), Ct);
        }, Ct);

        Socks5ProbeResult result = await new UpstreamSocksProbe(TimeSpan.FromSeconds(2)).ProbeAsync(port, Ct);

        await server;
        Assert.Equal(Socks5ProbeStatus.NotSocks5, result.Status);
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

    [Fact]
    public async Task Listener_owner_resolves_to_the_real_current_process_path()
    {
        using var listener = StartListener(out int port);
        var resolver = new TcpListenerOwnerResolver();
        IReadOnlyList<TcpListenerOwner> owners = Array.Empty<TcpListenerOwner>();
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            owners = resolver.Resolve(port, Ct);
            if (owners.Any(owner => owner.ProcessId == (uint)Environment.ProcessId))
                break;
            await Task.Delay(50, Ct);
        }

        TcpListenerOwner owner = Assert.Single(owners, owner => owner.ProcessId == (uint)Environment.ProcessId);
        Assert.True(owner.IsResolved);
        Assert.True(File.Exists(owner.CanonicalExecutablePath), owner.CanonicalExecutablePath);
    }

    [Fact]
    public async Task Upstream_monitor_requires_resolved_non_self_owner()
    {
        using var listener = StartListener(out int port);
        Task server = RespondToGreetingAsync(listener, [0x05, 0x00]);
        var monitor = new UpstreamMonitor(port, forbiddenPaths: [Environment.ProcessPath!]);

        UpstreamStatusSnapshot snapshot = await monitor.CheckAsync(forceProbe: true, Ct);

        await server;
        await monitor.DisposeAsync();
        Assert.False(snapshot.IsReady);
        Assert.Contains("自身", snapshot.Error, StringComparison.Ordinal);
        Assert.Contains(snapshot.OwnerPaths, path => string.Equals(
            path,
            new ExecutablePathCanonicalizer().Canonicalize(Environment.ProcessPath!),
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Upstream_monitor_reports_ready_for_a_real_provider()
    {
        using var listener = StartListener(out int port);
        Task server = RespondToGreetingAsync(listener, [0x05, 0x00]);
        await using var monitor = new UpstreamMonitor(port);

        UpstreamStatusSnapshot snapshot = await monitor.CheckAsync(forceProbe: true, Ct);

        await server;
        Assert.True(snapshot.IsReady, snapshot.Error ?? snapshot.Probe.Message);
        Assert.Contains(snapshot.OwnerPaths, path => File.Exists(path));
    }

    private static TcpListener StartListener(out int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return listener;
    }

    private static Task RespondToGreetingAsync(TcpListener listener, byte[] response)
        => Task.Run(async () =>
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(Ct);
            NetworkStream stream = client.GetStream();
            byte[] greeting = new byte[3];
            await ReadExactlyAsync(stream, greeting, Ct);
            Assert.Equal([0x05, 0x01, 0x00], greeting);
            await stream.WriteAsync(response, Ct);
        }, Ct);

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int count = await stream.ReadAsync(buffer[offset..], ct);
            if (count == 0)
                throw new EndOfStreamException();
            offset += count;
        }
    }
}
