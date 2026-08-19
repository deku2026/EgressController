using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using EgressController.Core.Contracts;
using EgressController.Core.Routing;
using EgressController.Core.Diagnostics;
using EgressController.Proxy.Parsing;

namespace EgressController.Proxy.Server;

/// <summary>
/// Loopback-only HTTP(S) routing proxy (plan §Step 04/11). Decides each connection/request through
/// <see cref="IProxyRouteSource"/>: an ESIM route connects interface-bound directly (no upstream
/// CONNECT), an upstream route tunnels via the configured proxy. CONNECT → byte relay after the
/// route's tunnel. Plain HTTP → per-request forward (origin-form for ESIM, absolute-form for
/// upstream) with Connection: close, then relay + close. Fail-closed: 502 with no DIRECT fallback.
/// Listener binds loopback only. Injectable <see cref="ConnectionLog"/> for observability.
/// </summary>
public sealed class LocalProxyServer : IAsyncDisposable
{
    private readonly IProxyRouteSource _routes;
    private readonly IConnectionLog? _log;
    private readonly IProxySourceResolver? _sourceResolver;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<Guid, ActiveConnection> _connections = new();
    private Task? _acceptLoop;
    private int _rejectAll;

    public LocalProxyServer(
        IProxyRouteSource routes,
        int port = 18080,
        IConnectionLog? log = null,
        IProxySourceResolver? sourceResolver = null)
    {
        _routes = routes ?? throw new ArgumentNullException(nameof(routes));
        _log = log;
        _sourceResolver = sourceResolver;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    /// <summary>Back-compat: everything routes to the given upstream (Step 04 behavior).</summary>
    public LocalProxyServer(IUpstreamProxyConnector upstream, int port = 18080, IConnectionLog? log = null)
        : this(new AllUpstreamRouteSource(upstream), port, log)
    {
    }

    public int BoundPort => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public int ActiveConnections => _connections.Count;
    public bool IsRejectingAll => Volatile.Read(ref _rejectAll) != 0;

    /// <summary>Cancel and close every connection currently handled by this listener.</summary>
    public int CloseAllConnections()
    {
        ActiveConnection[] connections = _connections.Values.ToArray();
        foreach (ActiveConnection connection in connections)
            connection.Close();
        return connections.Length;
    }

    /// <summary>
    /// Atomically changes the listener's fail-closed gate. Enabling it closes every current
    /// connection and refuses newly accepted sockets before request parsing or route selection.
    /// No rejected request can fall through to either ESIM or the upstream proxy.
    /// </summary>
    public int SetRejectAll(bool reject)
    {
        Volatile.Write(ref _rejectAll, reject ? 1 : 0);
        return reject ? CloseAllConnections() : 0;
    }

    public void Start()
    {
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_stop.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (SocketException) when (ct.IsCancellationRequested) { return; }
            catch (OperationCanceledException) { return; }

            if (IsRejectingAll)
            {
                client.Dispose();
                continue;
            }

            _ = Task.Run(() => HandleAsync(client, ct));
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken serverCancellation)
    {
        Guid connectionId = Guid.NewGuid();
        using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(serverCancellation);
        var activeConnection = new ActiveConnection(client, connectionCancellation);
        _connections[connectionId] = activeConnection;

        try
        {
            // Close the accept/check race when the fail-closed gate changes between Accept and
            // registering this connection.
            if (IsRejectingAll)
            {
                activeConnection.Close();
                return;
            }

            CancellationToken ct = connectionCancellation.Token;
            using (client)
            using (var clientStream = client.GetStream())
            {
                ProxySource? source = ResolveSource(client, ct);
                (byte[]? buf, int _) = await ReadHeadAsync(clientStream, ct).ConfigureAwait(false);
                if (buf is null)
                {
                    await WriteSimpleErrorAsync(clientStream, 400, "bad request head").ConfigureAwait(false);
                    return;
                }

                var parsed = ProxyRequestParser.Parse(buf);
                if (parsed.Error != ProxyRequestParseError.None)
                {
                    _log?.Write(Event(parsed, source, Egress.UpstreamProxy, RouteReason.SourceUnknown,
                        null, parsed.ErrorDetail, null, ConnectionStatus.Failed, 0));
                    await WriteSimpleErrorAsync(clientStream, 400, parsed.ErrorDetail).ConfigureAwait(false);
                    return;
                }

                if (parsed.Kind == ProxyRequestKind.Connect)
                    await HandleConnectAsync(clientStream, parsed, source, ct).ConfigureAwait(false);
                else
                    await HandlePlainAsync(clientStream, buf, parsed, source, ct).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Per-connection failure closes the socket; the server keeps running.
        }
        finally
        {
            _connections.TryRemove(connectionId, out _);
        }
    }

    private ProxySource? ResolveSource(TcpClient client, CancellationToken ct)
    {
        if (_sourceResolver is null
            || client.Client.RemoteEndPoint is not IPEndPoint clientLocal
            || client.Client.LocalEndPoint is not IPEndPoint listenerLocal)
            return null;

        try
        {
            return _sourceResolver.Resolve(clientLocal, listenerLocal, ct);
        }
        catch
        {
            // A process may exit between accept and the TCP table snapshot.  Unknown is a safe
            // result: the route engine will continue with Domain/Default, never ManagedApp.
            return null;
        }
    }

    private async Task HandleConnectAsync(NetworkStream clientStream, ParsedProxyRequest parsed, ProxySource? source, CancellationToken ct)
    {
        ProxyRoute? route = _routes.Resolve(parsed.Host, parsed.Port, source);
        _log?.Write(Event(parsed, source, route?.Decision.Egress ?? Egress.UpstreamProxy, route?.Decision.Reason ?? RouteReason.SourceUnknown,
            route?.Decision.MatchedRule?.RuleSetName, route?.Decision.MatchedRule?.RuleText, null, ConnectionStatus.Decided, 0));

        if (route is null)
        {
            await WriteSimpleErrorAsync(clientStream, 400, "no route").ConfigureAwait(false);
            return;
        }

        Stream tunnel;
        string iface = route.Target.Description;
        try
        {
            tunnel = await route.Target.ConnectTunnelAsync(parsed.Host, parsed.Port, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            _log?.Write(Event(parsed, source, route.Decision.Egress, route.Decision.Reason,
                route.Decision.MatchedRule?.RuleSetName, route.Decision.MatchedRule?.RuleText,
                iface, ConnectionStatus.Failed, 0));
            await WriteSimpleErrorAsync(clientStream, 502, "target unreachable (fail-closed)").ConfigureAwait(false);
            return;
        }

        try
        {
            await WriteAsciiAsync(clientStream, "HTTP/1.1 200 Connection established\r\n\r\n").ConfigureAwait(false);
            _log?.Write(Event(parsed, source, route.Decision.Egress, route.Decision.Reason,
                route.Decision.MatchedRule?.RuleSetName, route.Decision.MatchedRule?.RuleText,
                iface, ConnectionStatus.Established, 0));
            await RelayAsync(clientStream, tunnel, ct).ConfigureAwait(false);
        }
        finally
        {
            _log?.Write(Event(parsed, source, route.Decision.Egress, route.Decision.Reason,
                route.Decision.MatchedRule?.RuleSetName, route.Decision.MatchedRule?.RuleText,
                iface, ConnectionStatus.Closed, 0));
            tunnel.Dispose();
        }
    }

    private async Task HandlePlainAsync(
        NetworkStream clientStream, byte[] buf, ParsedProxyRequest parsed, ProxySource? source, CancellationToken ct)
    {
        ProxyRoute? route = _routes.Resolve(parsed.Host, parsed.Port, source);
        if (route is null)
        {
            await WriteSimpleErrorAsync(clientStream, 400, "no route").ConfigureAwait(false);
            return;
        }

        _log?.Write(Event(parsed, source, route.Decision.Egress, route.Decision.Reason,
            route.Decision.MatchedRule?.RuleSetName, route.Decision.MatchedRule?.RuleText,
            route.Target.Description, ConnectionStatus.Decided, 0));

        Stream target;
        string iface = route.Target.Description;
        try
        {
            target = await route.Target.OpenNextHopAsync(parsed.Host, parsed.Port, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            _log?.Write(Event(parsed, source, route.Decision.Egress, route.Decision.Reason,
                route.Decision.MatchedRule?.RuleSetName, route.Decision.MatchedRule?.RuleText,
                iface, ConnectionStatus.Failed, 0));
            await WriteSimpleErrorAsync(clientStream, 502, "target unreachable (fail-closed)").ConfigureAwait(false);
            return;
        }

        try
        {
            if (route.Decision.Egress == Egress.Esim)
                await ForwardOriginFormAsync(target, buf, parsed, clientStream, ct).ConfigureAwait(false);
            else
                await ForwardAbsoluteFormAsync(target, buf, parsed, clientStream, ct).ConfigureAwait(false);
            _log?.Write(Event(parsed, source, route.Decision.Egress, route.Decision.Reason,
                route.Decision.MatchedRule?.RuleSetName, route.Decision.MatchedRule?.RuleText,
                iface, ConnectionStatus.Established, 0));
        }
        catch (Exception)
        {
            // upstream/ESIM broke mid-flight; close silently.
        }
        finally
        {
            _log?.Write(Event(parsed, source, route.Decision.Egress, route.Decision.Reason,
                route.Decision.MatchedRule?.RuleSetName, route.Decision.MatchedRule?.RuleText,
                iface, ConnectionStatus.Closed, 0));
            target.Dispose();
        }
    }

    private static string OriginForm(ParsedProxyRequest p)
        => p.TargetUri is { } u ? u.PathAndQuery : "/";

    /// <summary>Forward a plain HTTP request to a DIRECT origin (ESIM) using origin-form + Host.</summary>
    private async Task ForwardOriginFormAsync(
        Stream upstream, byte[] buf, ParsedProxyRequest parsed, NetworkStream clientStream, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append($"{parsed.Method} {OriginForm(parsed)} HTTP/1.1\r\n");
        sb.Append($"Host: {parsed.Host}{(parsed.Port == 80 ? "" : $":{parsed.Port}")}\r\n");
        foreach (var h in parsed.ForwardHeaders.Where(h => h.Key != "host"))
            sb.Append($"{h.Key}: {h.Value}\r\n");
        sb.Append("Connection: close\r\n\r\n");
        await WriteAsciiAsync(upstream, sb.ToString()).ConfigureAwait(false);

        await ForwardBodyAsync(clientStream, buf, parsed, upstream, ct).ConfigureAwait(false);
        await CopyToAsync(upstream, clientStream, ct).ConfigureAwait(false);
    }

    /// <summary>Forward a plain HTTP request to the upstream proxy using absolute-form (V1).</summary>
    private async Task ForwardAbsoluteFormAsync(
        Stream upstream, byte[] buf, ParsedProxyRequest parsed, NetworkStream clientStream, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append($"{parsed.Method} {parsed.TargetUri!.AbsoluteUri} HTTP/1.1\r\n");
        foreach (var h in parsed.ForwardHeaders)
            sb.Append($"{h.Key}: {h.Value}\r\n");
        sb.Append("Connection: close\r\n\r\n");
        await WriteAsciiAsync(upstream, sb.ToString()).ConfigureAwait(false);

        await ForwardBodyAsync(clientStream, buf, parsed, upstream, ct).ConfigureAwait(false);
        await CopyToAsync(upstream, clientStream, ct).ConfigureAwait(false);
    }

    private static async Task ForwardBodyAsync(
        NetworkStream client, byte[] initial, ParsedProxyRequest parsed, Stream upstream, CancellationToken ct)
    {
        string? cl = parsed.ForwardHeaders.FirstOrDefault(h => h.Key == "content-length").Value;
        string? te = parsed.ForwardHeaders.FirstOrDefault(h => h.Key == "transfer-encoding").Value;

        if (cl is not null && int.TryParse(cl, out int remaining))
        {
            int inBuf = initial.Length - parsed.BodyOffset;
            int take = Math.Min(remaining, inBuf);
            if (take > 0)
                await upstream.WriteAsync(initial.AsMemory(parsed.BodyOffset, take), ct).ConfigureAwait(false);
            remaining -= take;
            while (remaining > 0)
            {
                byte[] chunk = new byte[Math.Min(remaining, 8192)];
                int n = await client.ReadAsync(chunk, ct).ConfigureAwait(false);
                if (n == 0)
                    throw new IOException("client closed during request body");
                await upstream.WriteAsync(chunk.AsMemory(0, n), ct).ConfigureAwait(false);
                remaining -= n;
            }
        }
        else if (te is not null && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            if (initial.Length - parsed.BodyOffset > 0)
                await upstream.WriteAsync(initial.AsMemory(parsed.BodyOffset)).ConfigureAwait(false);
            byte[] chunk = new byte[8192];
            while (true)
            {
                int n = await client.ReadAsync(chunk, ct).ConfigureAwait(false);
                if (n == 0) break;
                await upstream.WriteAsync(chunk.AsMemory(0, n), ct).ConfigureAwait(false);
            }
        }
    }

    private static ConnectionEvent Event(ParsedProxyRequest parsed, ProxySource? source, Egress egress, RouteReason reason,
        string? ruleSet, string? ruleText, string? iface, ConnectionStatus status, long bytes)
        => new(DateTimeOffset.UtcNow, source?.Pid, source?.ProcessName ?? string.Empty, source?.FinalExePath,
            source?.SessionId, parsed.Host, parsed.Port,
            egress, reason, ruleSet, ruleText, iface ?? string.Empty, status, bytes, TimeSpan.Zero);

    private static async Task RelayAsync(Stream left, Stream right, CancellationToken ct)
    {
        var a = CopyToAsync(left, right, ct);
        var b = CopyToAsync(right, left, ct);
        await Task.WhenAny(a, b).ConfigureAwait(false);
    }

    private static async Task CopyToAsync(Stream from, Stream to, CancellationToken ct)
    {
        try
        {
            byte[] buffer = new byte[64 * 1024];
            int n;
            while ((n = await from.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                await to.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // mirror feedback loop; acceptable to stop on either side closing
        }
    }

    private static async Task<(byte[]? Data, int HeaderEnd)> ReadHeadAsync(Stream stream, CancellationToken ct)
    {
        byte[] term = "\r\n\r\n"u8.ToArray();
        var buffer = new MemoryStream();
        byte[] tmp = new byte[4096];
        while (buffer.Length <= ProxyRequestParser.MaxHeaderBytes + ProxyRequestParser.MaxRequestLineBytes)
        {
            if (buffer.Length >= 4)
            {
                byte[] cur = buffer.ToArray();
                bool found = true;
                for (int j = 0; j < 4; j++)
                    if (cur[cur.Length - 4 + j] != term[j]) { found = false; break; }
                if (found)
                {
                    byte[] all = buffer.ToArray();
                    return (all, all.Length - 4);
                }
            }
            int n = await stream.ReadAsync(tmp, ct).ConfigureAwait(false);
            if (n == 0)
                return (null, 0);
            buffer.Write(tmp, 0, n);
        }
        return (null, 0);
    }

    private static Task WriteAsciiAsync(Stream s, string text, CancellationToken ct = default)
        => s.WriteAsync(Encoding.ASCII.GetBytes(text), ct).AsTask();

    private static async Task WriteSimpleErrorAsync(Stream s, int status, string reason)
    {
        string body = $"<html><body><h1>{status}</h1><p>{reason}</p></body></html>";
        await WriteAsciiAsync(s, $"HTTP/1.1 {status} {Reason(status)}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}").ConfigureAwait(false);
    }

    private static string Reason(int status) => status switch
    {
        400 => "Bad Request",
        502 => "Bad Gateway",
        _ => "Error",
    };

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        CloseAllConnections();
        _listener.Stop();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _stop.Dispose();
    }

    private sealed class ActiveConnection(TcpClient client, CancellationTokenSource cancellation)
    {
        public void Close()
        {
            try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
            try { client.Close(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>Route source that sends everything to one upstream (Step 04 default).</summary>
    private sealed class AllUpstreamRouteSource(IUpstreamProxyConnector upstream) : IProxyRouteSource
    {
        private readonly UpstreamConnectTarget _target = new(upstream);
        public ProxyRoute? Resolve(string host, int port, ProxySource? source = null)
            => new(RouteDecision.DefaultUpstream, _target);

        private sealed class UpstreamConnectTarget(IUpstreamProxyConnector upstream) : IConnectTarget
        {
            public string Description => $"upstream {upstream.Endpoint}";
            public ValueTask<Stream> ConnectTunnelAsync(string host, int port, CancellationToken ct)
                => upstream.ConnectTunnelAsync(host, port, ct);
            public ValueTask<Stream> OpenNextHopAsync(string host, int port, CancellationToken ct)
                => upstream.OpenNextHopAsync(ct);
        }
    }
}
