using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using EgressController.Core.Contracts;
using EgressController.Proxy.Server;
using EgressController.Transport.Upstream;

namespace EgressController.Proxy.Tests;

/// <summary>
/// Loopback-only end-to-end tests: LocalProxyServer → (minimal) fake HTTP upstream → loopback
/// origin. No external network. Covers CONNECT tunnel relay, plain-HTTP forward+relay, and
/// fail-closed 502 when the upstream is down.
/// </summary>
public class LocalProxyServerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Connect_tunnel_relays_bytes_through_proxy_and_upstream()
    {
        await using var origin = new EchoOrigin();
        await using var proxy = StartProxy(new DirectPassThroughUpstream(origin.Port));

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.BoundPort, Ct);
        using var io = new StreamReaderAndWriter(client);

        await io.Writer.WriteLineAsync(("CONNECT 127.0.0.1:" + origin.Port + " HTTP/1.1").AsMemory(), Ct);
        await io.Writer.WriteLineAsync(("Host: 127.0.0.1:" + origin.Port).AsMemory(), Ct);
        await io.Writer.WriteLineAsync(ReadOnlyMemory<char>.Empty, Ct);
        await io.Writer.FlushAsync(Ct);

        string? greeting = await io.Reader.ReadLineAsync(Ct);
        Assert.StartsWith("HTTP/1.1 200", greeting ?? "");
        // The 200 line is followed by an empty line terminating the header section.
        string? blank = await io.Reader.ReadLineAsync(Ct);
        Assert.Equal("", blank ?? "");

        await io.Writer.WriteAsync("PING-CONNECT\r\n".AsMemory(), Ct);
        await io.Writer.FlushAsync(Ct);
        string? echoed = await io.Reader.ReadLineAsync(Ct);
        Assert.Equal("ECHO:PING-CONNECT", echoed);
    }

    [Fact]
    public async Task Plain_http_get_is_forwarded_and_response_relayed()
    {
        await using var upstream = new MinimalUpstream();
        await using var proxy = StartProxy(upstream.Port);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.BoundPort, Ct);
        using var io = new StreamReaderAndWriter(client);

        await io.Writer.WriteAsync($"GET http://127.0.0.1:9/hello HTTP/1.1\r\nHost: 127.0.0.1:9\r\n\r\n".AsMemory(), Ct);
        await io.Writer.FlushAsync(Ct);

        string response = await io.Reader.ReadToEndAsync(Ct);
        Assert.Contains("HTTP/1.1 200 OK", response);
        Assert.Contains("hello!", response);
    }

    [Fact]
    public async Task Upstream_down_yields_502_fail_closed()
    {
        await using var proxy = StartProxy(DeadPort());
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.BoundPort, Ct);
        using var io = new StreamReaderAndWriter(client);

        await io.Writer.WriteAsync("CONNECT 127.0.0.1:1 HTTP/1.1\r\nHost: x\r\n\r\n".AsMemory(), Ct);
        await io.Writer.FlushAsync(Ct);
        string response = await io.Reader.ReadToEndAsync(Ct);
        Assert.Contains("502", response);
    }

    [Fact]
    public async Task Close_all_connections_stops_an_active_tunnel()
    {
        await using var origin = new HoldingOrigin();
        await using var proxy = StartProxy(new DirectPassThroughUpstream(origin.Port));

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.BoundPort, Ct);
        using var io = new StreamReaderAndWriter(client);
        await io.Writer.WriteAsync($"CONNECT 127.0.0.1:{origin.Port} HTTP/1.1\r\nHost: x\r\n\r\n".AsMemory(), Ct);
        await io.Writer.FlushAsync(Ct);

        Assert.StartsWith("HTTP/1.1 200", await io.Reader.ReadLineAsync(Ct) ?? "");
        Assert.Equal("", await io.Reader.ReadLineAsync(Ct) ?? "");

        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline && proxy.ActiveConnections == 0)
            await Task.Delay(20, Ct);

        Assert.Equal(1, proxy.CloseAllConnections());
        deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline && proxy.ActiveConnections != 0)
            await Task.Delay(20, Ct);
        Assert.Equal(0, proxy.ActiveConnections);
    }

    [Fact]
    public async Task Reject_all_closes_current_tunnel_and_refuses_new_sockets_until_reopened()
    {
        await using var origin = new HoldingOrigin();
        await using var proxy = StartProxy(new DirectPassThroughUpstream(origin.Port));

        using var active = new TcpClient();
        await active.ConnectAsync(IPAddress.Loopback, proxy.BoundPort, Ct);
        using var activeIo = new StreamReaderAndWriter(active);
        await activeIo.Writer.WriteAsync($"CONNECT 127.0.0.1:{origin.Port} HTTP/1.1\r\nHost: x\r\n\r\n".AsMemory(), Ct);
        await activeIo.Writer.FlushAsync(Ct);
        Assert.StartsWith("HTTP/1.1 200", await activeIo.Reader.ReadLineAsync(Ct) ?? "");
        Assert.Equal("", await activeIo.Reader.ReadLineAsync(Ct) ?? "");

        Assert.Equal(1, proxy.SetRejectAll(true));
        Assert.True(proxy.IsRejectingAll);
        await WaitUntilAsync(() => proxy.ActiveConnections == 0);

        using var rejected = new TcpClient();
        await rejected.ConnectAsync(IPAddress.Loopback, proxy.BoundPort, Ct);
        Assert.True(await IsClosedWithoutResponseAsync(rejected));

        proxy.SetRejectAll(false);
        Assert.False(proxy.IsRejectingAll);

        using var restored = new TcpClient();
        await restored.ConnectAsync(IPAddress.Loopback, proxy.BoundPort, Ct);
        using var restoredIo = new StreamReaderAndWriter(restored);
        await restoredIo.Writer.WriteAsync($"CONNECT 127.0.0.1:{origin.Port} HTTP/1.1\r\nHost: x\r\n\r\n".AsMemory(), Ct);
        await restoredIo.Writer.FlushAsync(Ct);
        Assert.StartsWith("HTTP/1.1 200", await restoredIo.Reader.ReadLineAsync(Ct) ?? "");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Proxy condition was not reached.");
            await Task.Delay(20, Ct);
        }
    }

    private static async Task<bool> IsClosedWithoutResponseAsync(TcpClient client)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            byte[] one = new byte[1];
            return await client.GetStream().ReadAsync(one, timeout.Token) == 0;
        }
        catch (IOException)
        {
            return true;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    private static LocalProxyServer StartProxy(IUpstreamProxyConnector upstream)
    {
        var proxy = new LocalProxyServer(upstream, port: 0);
        proxy.Start();
        return proxy;
    }

    private static LocalProxyServer StartProxy(int upstreamPort)
        => StartProxy(new UpstreamHttpProxyConnector("127.0.0.1", upstreamPort) { ConnectTimeout = TimeSpan.FromSeconds(4) });

    /// <summary>A trivial upstream that CONNECTs straight to the origin (validates LocalProxy's relay).</summary>
    private sealed class DirectPassThroughUpstream(int originPort) : IUpstreamProxyConnector
    {
        public string Endpoint => $"direct:{originPort}";
        public async ValueTask<Stream> ConnectTunnelAsync(string host, int port, CancellationToken ct)
        {
            var t = new TcpClient();
            await t.ConnectAsync(IPAddress.Loopback, originPort, ct);
            return t.GetStream();
        }

        public ValueTask<Stream> OpenNextHopAsync(CancellationToken ct)
            => throw new NotSupportedException("not used in CONNECT test");
    }

    private static int DeadPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private sealed class StreamReaderAndWriter : IDisposable
    {
        private readonly Socket _socket;
        public StreamReader Reader { get; }
        public StreamWriter Writer { get; }

        public StreamReaderAndWriter(TcpClient client)
        {
            _socket = client.Client;
            var s = client.GetStream();
            Reader = new StreamReader(s, Encoding.ASCII, false, 1024, leaveOpen: true);
            Writer = new StreamWriter(s, Encoding.ASCII, 1024, leaveOpen: true) { AutoFlush = false, NewLine = "\r\n" };
        }

        public void Dispose() => _socket.Dispose();
    }

    private sealed class EchoOrigin : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public EchoOrigin()
        {
            _listener.Start();
            _ = Task.Run(AcceptLoop);
        }

        private async Task AcceptLoop()
        {
            while (true)
            {
                TcpClient c;
                try { c = await _listener.AcceptTcpClientAsync(); }
                catch { return; }
                _ = Task.Run(() => HandleAsync(c));
            }
        }

        private static async Task HandleAsync(TcpClient client)
        {
            using (client)
            using (var r = new StreamReader(client.GetStream(), Encoding.ASCII, false, 1024, leaveOpen: true))
            using (var w = new StreamWriter(client.GetStream(), Encoding.ASCII, 1024, leaveOpen: true))
            {
                string? line = await r.ReadLineAsync();
                if (line is not null)
                    await w.WriteAsync("ECHO:" + line + "\r\n");
                await w.FlushAsync();
            }
        }

        public ValueTask DisposeAsync()
        {
            _listener.Stop();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class HoldingOrigin : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<TcpClient> _clients = new();
        private readonly Task _acceptLoop;

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public HoldingOrigin()
        {
            _listener.Start();
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (true)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _clients.Add(client);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            foreach (TcpClient client in _clients)
                client.Dispose();
            try { await _acceptLoop; } catch (OperationCanceledException) { }
            _stop.Dispose();
        }
    }

    private sealed class MinimalUpstream : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public MinimalUpstream()
        {
            _listener.Start();
            _ = Task.Run(AcceptLoop);
        }

        private async Task AcceptLoop()
        {
            while (true)
            {
                TcpClient c;
                try { c = await _listener.AcceptTcpClientAsync(); }
                catch { return; }
                _ = Task.Run(() => HandleAsync(c), TestContext.Current.CancellationToken);
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                // Read the full request head (raw, not through a buffered reader/Writer) so no
                // bytes are double-consumed, then act on it.
                byte[] head = await ReadRawHeadAsync(stream);
                string headText = Encoding.ASCII.GetString(head);
                string firstLine = headText.Split('\r')[0];
                string[] parts = firstLine.Split(' ');

                if (parts.Length >= 2 && parts[0] == "CONNECT")
                {
                    string authority = parts[1];
                    int colon = authority.LastIndexOf(':');
                    int port = int.Parse(authority[(colon + 1)..]);
                    using var target = new TcpClient();
                    await target.ConnectAsync(IPAddress.Loopback, port);
                    var targetStream = target.GetStream();
                    await stream.WriteAsync("HTTP/1.1 200 Connection established\r\n\r\n"u8.ToArray());
                    await stream.FlushAsync();
                    var a = stream.CopyToAsync(targetStream);
                    var b = targetStream.CopyToAsync(stream);
                    await Task.WhenAny(a, b);
                }
                else
                {
                    await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 6\r\nConnection: close\r\n\r\nhello!"u8.ToArray());
                    await stream.FlushAsync();
                }
            }
        }

        private static async Task<byte[]> ReadRawHeadAsync(Stream stream)
        {
            var buffer = new MemoryStream();
            byte[] tmp = new byte[1024];
            while (buffer.Length <= 64 * 1024)
            {
                byte[] cur = buffer.ToArray();
                if (cur.Length >= 4 && cur.AsSpan(^4..).SequenceEqual("\r\n\r\n"u8))
                    return cur;
                int n = await stream.ReadAsync(tmp);
                if (n == 0)
                    return buffer.ToArray();
                buffer.Write(tmp, 0, n);
            }
            return buffer.ToArray();
        }

        public ValueTask DisposeAsync()
        {
            _listener.Stop();
            return ValueTask.CompletedTask;
        }
    }
}
