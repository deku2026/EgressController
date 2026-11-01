using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using EgressController.Policy;

namespace EgressController.Runtime;

public sealed record InterfaceConnectivityResult(bool Available, string InterfaceId, string BsdName, string Endpoint, string FailureCode, double Milliseconds);

public sealed class InterfaceConnectivity(IPhysicalSocketBinder binder, IInterfaceDnsResolver resolver,
    Func<SocketsHttpHandler, HttpMessageHandler>? handlerFactory = null)
{
    private static readonly (string Name, string Url)[] Endpoints = [("cloudflare-204", "https://cp.cloudflare.com/generate_204"), ("google-204", "https://www.gstatic.com/generate_204")];
    // Setup guidance only. TUN startup remains independent of physical link/IP readiness.
    public static string? ReadinessFailure(bool active, IEnumerable<string> addresses)
    {
        if (!active) return "interface.inactive";
        return addresses.Any(UsableAddress) ? null : "interface.no-address";
    }
    private static bool UsableAddress(string value)
    {
        if (!IPAddress.TryParse(value, out var address)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return false;
        if (address.AddressFamily == AddressFamily.InterNetworkV6) return !address.IsIPv6LinkLocal && !address.IsIPv6Multicast;
        var bytes = address.GetAddressBytes();
        return bytes[0] != 0 && bytes[0] < 224 && !(bytes[0] == 169 && bytes[1] == 254);
    }
    public async Task<InterfaceConnectivityResult> CheckAsync(InterfaceBinding binding, CancellationToken token = default)
    {
        var connector = new BoundHttpsResourceTransport(binder, resolver);
        InterfaceConnectivityResult result = new(false, binding.StableId, binding.BsdName, "", "network.failed", 0);
        foreach (var endpoint in Endpoints)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(6));
            using var handler = new SocketsHttpHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false,
                ConnectCallback = (context, cancellation) => connector.ConnectAsync(context.DnsEndPoint, binding, cancellation) };
            using var client = new HttpClient(handlerFactory?.Invoke(handler) ?? handler) { Timeout = Timeout.InfiniteTimeSpan };
            var watch = Stopwatch.StartNew();
            try
            {
                using var response = await client.GetAsync(endpoint.Url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                bool success = response.StatusCode == HttpStatusCode.NoContent;
                result = new(success, binding.StableId, binding.BsdName, endpoint.Name, success ? "" : "http." + (int)response.StatusCode, watch.Elapsed.TotalMilliseconds);
                if (success) return result;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { result = new(false, binding.StableId, binding.BsdName, endpoint.Name, "timeout", watch.Elapsed.TotalMilliseconds); }
            catch (Exception error) when (error is IOException or HttpRequestException or System.Net.Sockets.SocketException)
            { result = new(false, binding.StableId, binding.BsdName, endpoint.Name, NetworkFailure.Code(error), watch.Elapsed.TotalMilliseconds); }
        }
        return result;
    }
}

public static class NetworkFailure
{
    // Only fixed classification strings leave this boundary; raw node URLs, UUIDs and passwords never do.
    public static string Code(Exception error)
    {
        var causes = Causes(error).ToArray();
        if (causes.OfType<ResourceTransportFailure>().FirstOrDefault() is { } transport) return transport.Code;
        // Stack frames include DNS class names even when the real failure is socket routing.
        string classification = Classify(string.Join("\n", causes.Select(cause => cause.Message)));
        if (classification == "interface.bind") return classification;
        if (causes.Any(cause => cause is TimeoutException or OperationCanceledException)) return "timeout";
        foreach (var socket in causes.OfType<SocketException>())
        {
            if (socket.SocketErrorCode is SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown or SocketError.AddressNotAvailable) return "network.unreachable";
            if (socket.SocketErrorCode == SocketError.ConnectionRefused) return "connection.refused";
            if (socket.SocketErrorCode == SocketError.TimedOut) return "timeout";
        }
        if (causes.Any(cause => cause is System.Security.Authentication.AuthenticationException)) return "tls.failed";
        return classification;
    }
    private static IEnumerable<Exception> Causes(Exception error)
    {
        yield return error;
        if (error is AggregateException aggregate)
        { foreach (var inner in aggregate.InnerExceptions) foreach (var cause in Causes(inner)) yield return cause; }
        else if (error.InnerException is { } inner)
        { foreach (var cause in Causes(inner)) yield return cause; }
    }
    public static string Classify(string text)
    {
        string value = text.ToLowerInvariant();
        if (value.Contains("bound_if") || value.Contains("binding") || value.Contains("failed to bind") || value.Contains("no such interface")) return "interface.bind";
        if (value.Contains("no route") || value.Contains("network is unreachable") || value.Contains("cannot assign")) return "network.unreachable";
        if (value.Contains("dns") || value.Contains("resolve") || value.Contains("no such host")) return "dns.failed";
        if (value.Contains("certificate") || value.Contains("tls") || value.Contains("handshake") || value.Contains("reality")) return "tls.failed";
        if (value.Contains("refused")) return "connection.refused";
        if (value.Contains("timeout") || value.Contains("timed out") || value.Contains("deadline")) return "timeout";
        return "connection.failed";
    }
    public static string Label(string? code) => code switch
    {
        "interface.inactive" => "网卡尚未激活", "interface.no-address" => "网卡尚未获取可用 IP",
        "interface.bind" => "绑定失败", "dns.failed" => "解析失败", "tls.failed" => "握手失败", "network.unreachable" => "无路由",
        "connection.refused" => "拒绝连接", "core.exit" => "内核退出", "compile.failed" => "配置无效", "timeout" or null => "超时", _ => "连接失败"
    };
}
