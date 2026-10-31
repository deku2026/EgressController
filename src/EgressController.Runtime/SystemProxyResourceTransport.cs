using System.Net;
using System.Net.Sockets;

namespace EgressController.Runtime;

public enum ResourceProxyFailureKind { PacUnsupported, InvalidSettings }
public sealed class ResourceProxyConfigurationException(ResourceProxyFailureKind reason)
    : IOException(reason == ResourceProxyFailureKind.PacUnsupported
        ? "系统代理使用 PAC 自动配置，请配置明确的 HTTP/HTTPS 或 SOCKS 代理后重试。未执行 PAC，也未改走直连。"
        : "系统代理配置不可用，请检查 HTTP/HTTPS 或 SOCKS 代理设置后重试。未改走直连。")
{ public ResourceProxyFailureKind Reason { get; } = reason; }

/// <summary>A resolved proxy decision for one HTTPS destination. Its failure never retries direct.</summary>
public interface ISystemResourceProxyResolver { IWebProxy? Resolve(Uri destination); }
/// <summary>Resource-only HTTPS bootstrap. Uses the system's proxy/default route, never a TUN prerequisite.</summary>
public sealed class SystemProxyResourceTransport(ISystemResourceProxyResolver resolver,
    Func<SocketsHttpHandler, HttpMessageHandler>? httpHandlerFactory = null) : IResourceTransport
{
    public async Task<byte[]> DownloadAsync(ResourceDescriptor resource, CancellationToken cancellationToken)
    {
        if (resource.Exit.Kind != ResourceExitKind.SystemProxy || !resource.Exit.IsValid || !SafeHttps(resource.Url) || resource.MaximumBytes < 1)
            throw new ArgumentException("HTTPS and an explicit system resource exit are required.", nameof(resource));
        try
        {
            var proxyResolver = resolver;
            Uri url = resource.Url;
            for (int redirects = 0; redirects <= 5; redirects++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var proxy = proxyResolver.Resolve(url);
                using var handler = new SocketsHttpHandler
                {
                    AllowAutoRedirect = false, UseProxy = proxy is not null, Proxy = proxy,
                    UseCookies = false, AutomaticDecompression = DecompressionMethods.None
                };
                using var client = new HttpClient(httpHandlerFactory?.Invoke(handler) ?? handler) { Timeout = Timeout.InfiniteTimeSpan };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("EgressController/1.0");
                using var request = new HttpRequestMessage(HttpMethod.Get, url) { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    if (redirects == 5 || response.Headers.Location is not { } location) throw new IOException("Resource redirect limit or missing location.");
                    url = location.IsAbsoluteUri ? location : new Uri(url, location);
                    if (!SafeHttps(url)) throw new IOException("Resource redirect must remain HTTPS without embedded credentials.");
                    continue;
                }
                if (!response.IsSuccessStatusCode) throw new IOException("Resource returned an unsuccessful status.");
                if (response.Content.Headers.ContentEncoding.Count != 0 || response.Content.Headers.ContentLength is { } size && size > resource.MaximumBytes)
                    throw new IOException("Resource encoding or size is not valid.");
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var output = new MemoryStream(); byte[] buffer = new byte[65536];
                while (true)
                {
                    int count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (count == 0) break;
                    if (output.Length + count > resource.MaximumBytes) throw new IOException("Resource exceeds the size limit.");
                    output.Write(buffer, 0, count);
                }
                return output.ToArray();
            }
            throw new IOException("Resource redirect limit.");
        }
        catch (Exception error) when (error is HttpRequestException or SocketException or System.Security.Authentication.AuthenticationException or WebException)
        { throw new IOException("系统代理资源下载失败；请检查当前系统代理是否可用，原缓存已保留。"); }
    }
    private static bool SafeHttps(Uri url) => url.IsAbsoluteUri && url.Scheme == Uri.UriSchemeHttps && url.UserInfo.Length == 0 && url.Fragment.Length == 0;
}
