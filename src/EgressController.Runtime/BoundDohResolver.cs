using System.Net;
using System.Net.Http.Headers;
using EgressController.Policy;

namespace EgressController.Runtime;

/// <summary>RFC 8484 wire DNS over TLS. The literal endpoint and all query sockets use the requested physical interface.</summary>
public sealed class BoundDohResolver(IPhysicalSocketBinder binder, string endpoint, Func<SocketsHttpHandler, HttpMessageHandler>? handlerFactory = null) : IInterfaceDnsResolver
{
    public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, InterfaceBinding binding, CancellationToken cancellationToken)
    {
        if (!DnsEndpoint.IsValid(endpoint)) throw new IOException("Invalid encrypted DNS endpoint.");
        var uri = new Uri(endpoint);
        return new BoundDnsResolver(new Exchange(binder, uri, handlerFactory), IPAddress.Parse(uri.Host.Trim('[', ']')))
            .ResolveAsync(host, binding, cancellationToken);
    }
    private sealed class Exchange(IPhysicalSocketBinder binder, Uri endpoint, Func<SocketsHttpHandler, HttpMessageHandler>? handlerFactory) : IBoundDnsExchange
    {
        public async ValueTask<byte[]> ExchangeAsync(byte[] query, IPAddress server, InterfaceBinding binding, bool tcp, CancellationToken token)
        {
            using var handler = new SocketsHttpHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false,
                ConnectCallback = async (_, cancellation) =>
                {
                    var socket = new System.Net.Sockets.Socket(server.AddressFamily, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
                    try { binder.Bind(socket, binding); await socket.ConnectAsync(new IPEndPoint(server, 443), cancellation).ConfigureAwait(false); return new System.Net.Sockets.NetworkStream(socket, true); }
                    catch { socket.Dispose(); throw; }
                } };
            using var client = new HttpClient(handlerFactory?.Invoke(handler) ?? handler) { Timeout = Timeout.InfiniteTimeSpan };
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(query) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentType?.MediaType != "application/dns-message") throw new IOException("Encrypted DNS response was rejected.");
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var output = new MemoryStream(); byte[] buffer = new byte[2048];
            while (await input.ReadAsync(buffer, token).ConfigureAwait(false) is var count && count > 0)
            { if (output.Length + count > 65535) throw new IOException("Encrypted DNS response exceeds its limit."); output.Write(buffer, 0, count); }
            return output.ToArray();
        }
    }
}
