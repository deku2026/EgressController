using System.Diagnostics;
using System.Net;
using EgressController.Policy;

namespace EgressController.Runtime;

public sealed record InterfaceConnectivityResult(bool Available, string InterfaceId, string BsdName, string Endpoint, string FailureCode, double Milliseconds);

public sealed class InterfaceConnectivity(IPhysicalSocketBinder binder, IInterfaceDnsResolver resolver,
    Func<SocketsHttpHandler, HttpMessageHandler>? handlerFactory = null)
{
    private static readonly (string Name, string Url)[] Endpoints = [("cloudflare-204", "https://cp.cloudflare.com/generate_204"), ("google-204", "https://www.gstatic.com/generate_204")];
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
    public static string Code(Exception error) => Classify(error.ToString());
    public static string Classify(string text)
    {
        string value = text.ToLowerInvariant();
        if (value.Contains("bound_if") || value.Contains("binding") || value.Contains("failed to bind") || value.Contains("no such interface")) return "interface.bind";
        if (value.Contains("dns") || value.Contains("resolve") || value.Contains("no such host")) return "dns.failed";
        if (value.Contains("certificate") || value.Contains("tls") || value.Contains("handshake") || value.Contains("reality")) return "tls.failed";
        if (value.Contains("no route") || value.Contains("network is unreachable") || value.Contains("cannot assign")) return "network.unreachable";
        if (value.Contains("refused")) return "connection.refused";
        if (value.Contains("timeout") || value.Contains("timed out") || value.Contains("deadline")) return "timeout";
        return "connection.failed";
    }
    public static string Label(string? code) => code switch
    {
        "interface.bind" => "绑定失败", "dns.failed" => "解析失败", "tls.failed" => "握手失败", "network.unreachable" => "无路由",
        "connection.refused" => "拒绝连接", "core.exit" => "内核退出", "compile.failed" => "配置无效", "timeout" or null => "超时", _ => "连接失败"
    };
}
