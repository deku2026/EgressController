using System.Net;
using System.Net.Security;
using System.Text;
using EgressController.Core.Models;
using EgressController.Windows.Network;

// netprobe: diagnostic CLI for network baselines + ESIM direct.
//   netprobe interfaces
//   netprobe egress
//   netprobe upstream --host <h> --port <p>
//   netprobe esim [--guid <guid>] [--target <host:port>]     (default adapter name contains "ESIM")
//   netprobe monitor                                          (watch interface/route events N seconds)
// Exit 0 success, 2 usage, 3 probe failure.

string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "interfaces";

switch (sub)
{
    case "interfaces":
        return PrintInterfaces();
    case "egress":
        return PrintEgress();
    case "upstream":
        return PrintUpstream(args);
    case "esim":
        return PrintEsim(args);
    case "monitor":
        return Monitor(args);
    case "systemproxy":
        return SystemProxy(args);
    default:
        Console.Error.WriteLine("usage: netprobe interfaces | egress | upstream | esim | monitor | systemproxy");
        return 2;
}

int PrintInterfaces()
{
    var svc = new WindowsNetworkAdapterService();
    foreach (var a in svc.EnumerateAll())
    {
        Console.WriteLine($"GUID        {a.Identity.Guid}  ({(a.Identity.NameSnapshot.Length > 0 ? a.Identity.NameSnapshot : "(no friendly name)")})");
        Console.WriteLine($"  Name/Descr {a.Description}");
        Console.WriteLine($"  ifIndex    {a.IfIndex}   ipv6IfIndex={a.Ipv6IfIndex}   Luid=0x{a.Luid:x16}   {(a.IsUp ? "UP" : "DOWN")}");
        Console.WriteLine($"  Addr       {string.Join(", ", a.Addresses)}");
        Console.WriteLine($"  Gateways   {string.Join(", ", a.Gateways)}");
        Console.WriteLine($"  DNS        {string.Join(", ", a.DnsServers)}");
        Console.WriteLine();
    }
    return 0;
}

int PrintEgress()
{
    var svc = new WindowsNetworkAdapterService();
    int bestIfIndex = svc.GetDefaultRouteInterfaceIndex();
    NetworkAdapterInfo? pri = svc.GetByIfIndex(bestIfIndex);

    Console.WriteLine("EGRESS (PRIMARY / default route)");
    Console.WriteLine($"  bestIfIndex = {bestIfIndex}");
    Console.WriteLine($"  adapter     = {(pri is null ? "(not resolved)" : $"{pri.Identity.NameSnapshot}  GUID={pri.Identity.Guid}")}");

    string publicIp = FetchPublicIp(new HttpClientHandler { UseProxy = false });
    Console.WriteLine($"  public IP   = {publicIp}");
    return publicIp.StartsWith("error", StringComparison.OrdinalIgnoreCase) ? 3 : 0;
}

int PrintUpstream(string[] args)
{
    (string? host, int? port) = ParseHostPort(args, 1);
    if (host is null || port is null)
    {
        Console.Error.WriteLine("upstream requires --host <h> --port <p>");
        return 2;
    }

    var handler = new HttpClientHandler
    {
        UseProxy = true,
        Proxy = new WebProxy(new Uri($"http://{host}:{port}")),
    };
    string publicIp = FetchPublicIp(handler);
    Console.WriteLine("UPSTREAM PROXY");
    Console.WriteLine($"  endpoint    = {host}:{port} (HTTP-compatible)");
    Console.WriteLine($"  public IP   = {publicIp}");
    return publicIp.StartsWith("error", StringComparison.OrdinalIgnoreCase) ? 3 : 0;
}

int PrintEsim(string[] args)
{
    var svc = new WindowsNetworkAdapterService();
    string? guid = null;
    string target = "api.ipify.org:443";
    for (int i = 1; i < args.Length; i++)
    {
        if (args[i] == "--guid" && i + 1 < args.Length) guid = args[++i];
        else if (args[i] == "--target" && i + 1 < args.Length) target = args[++i];
    }

    NetworkAdapterInfo? esim = guid is not null && Guid.TryParse(guid, out var g)
        ? svc.GetByGuid(g)
        : svc.EnumerateAll().FirstOrDefault(a => a.Identity.NameSnapshot.Contains("ESIM", StringComparison.OrdinalIgnoreCase));

    if (esim is null)
    {
        Console.Error.WriteLine("ESIM adapter not found (pass --guid, or ensure an adapter name contains 'ESIM').");
        return 3;
    }

    int colon = target.LastIndexOf(':');
    string host = colon > 0 ? target[..colon] : target;
    int port = colon > 0 && int.TryParse(target[(colon + 1)..], out int p) ? p : 443;

    var connector = new EsimEgressConnector(new EsimDnsResolver(), new EsimSocketFactory())
    {
        ConnectTimeout = TimeSpan.FromSeconds(12),
    };

    Console.WriteLine("ESIM DIRECT");
    Console.WriteLine($"  adapter = {esim.Identity.NameSnapshot}  GUID={esim.Identity.Guid}  ifIndex={esim.IfIndex}");
    Console.WriteLine($"  target  = {host}:{port}  (resolved + connected pinned to ESIM interface)");

    Stream tcp;
    try
    {
        tcp = connector.ConnectAsync(host, port, esim, CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  error   = ESIM connect failed (fail-closed, no fallback): {ex.Message}");
        return 3;
    }

    using var ssl = new SslStream(tcp, leaveInnerStreamOpen: false);
    try
    {
        ssl.AuthenticateAsClient(host);
        byte[] req = Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n");
        ssl.Write(req);
        ssl.Flush();

        using var ms = new MemoryStream();
        ssl.CopyTo(ms);
        string response = Encoding.UTF8.GetString(ms.ToArray());
        int idx = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        string body = idx >= 0 ? response[(idx + 4)..].Trim() : response.Trim();
        Console.WriteLine($"  public IP via ESIM = {body}");
        return body.Length > 0 ? 0 : 3;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  error   = TLS/read failed over ESIM tunnel: {ex.GetType().Name}: {ex.Message}");
        return 3;
    }
}

int SystemProxy(string[] args)
{
    var mgr = new EgressController.Windows.SystemProxy.SystemProxyManager();
    string action = args.Length > 1 ? args[1].ToLowerInvariant() : "show";
    switch (action)
    {
        case "show":
        case "snapshot":
            var s = mgr.Snapshot();
            Console.WriteLine($"SYSTEM PROXY  enabled={s.Enabled}");
            Console.WriteLine($"  Server      = {s.Server}");
            Console.WriteLine($"  Override    = {s.ProxyOverride}");
            Console.WriteLine($"  AutoConfig  = {s.AutoConfigUrl}");
            Console.WriteLine($"  AutoDetect  = {s.AutoDetect}");
            return 0;
        case "acquire":
            mgr.Apply(EgressController.Core.Models.SystemProxyState.Ours());
            Console.WriteLine($"acquired -> server={mgr.Snapshot().Server}");
            return 0;
        case "restore":
            mgr.Apply(EgressController.Core.Models.SystemProxyState.Off);
            Console.WriteLine($"restored -> enabled={mgr.Snapshot().Enabled}");
            return 0;
        case "owned":
            bool owned = EgressController.Core.Models.SystemProxyStateComparer
                .StateEquivalent(mgr.Snapshot(), EgressController.Core.Models.SystemProxyState.Ours());
            Console.WriteLine(owned ? "OWNED(18080)" : "NOT-OWNED");
            return owned ? 0 : 1;
        default:
            Console.Error.WriteLine("systemproxy: show|snapshot|acquire|restore|owned");
            return 2;
    }
}

int Monitor(string[] args)
{
    int seconds = 12;
    for (int i = 1; i < args.Length; i++)
        if (args[i] == "--seconds" && i + 1 < args.Length && int.TryParse(args[++i], out int s)) seconds = s;

    var monitor = new WindowsNetworkInterfaceMonitor();
    monitor.Changed += e => Console.WriteLine($"  {DateTime.Now:HH:mm:ss.fff}  {e.Kind}  ifIndex={e.IfIndex}");
    monitor.Start();
    Console.WriteLine($"monitoring interface/route changes for {seconds}s (unplug/replug ESIM to see events)...");
    Thread.Sleep(TimeSpan.FromSeconds(seconds));
    monitor.DisposeAsync().AsTask().GetAwaiter().GetResult();
    return 0;
}

(string? Host, int? Port) ParseHostPort(string[] args, int start)
{
    string? host = null;
    int? port = null;
    for (int i = start; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--host" when i + 1 < args.Length: host = args[++i]; break;
            case "--port" when i + 1 < args.Length && int.TryParse(args[++i], out int p): port = p; break;
        }
    }
    return (host, port);
}

string FetchPublicIp(HttpMessageHandler handler)
{
    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        return http.GetStringAsync("https://api.ipify.org", cts.Token).GetAwaiter().GetResult().Trim();
    }
    catch (Exception ex)
    {
        return $"error: {ex.GetType().Name}: {ex.Message}";
    }
}