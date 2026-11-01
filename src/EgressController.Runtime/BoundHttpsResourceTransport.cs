using System.Net;
using System.Net.Sockets;
using EgressController.Policy;

namespace EgressController.Runtime;

/// <summary>Must bind this socket to the physical interface or throw; implementations must never retry unbound.</summary>
public interface IPhysicalSocketBinder { void Bind(Socket socket, InterfaceBinding binding); }
/// <summary>Resolution itself must use the supplied physical interface, without the system/default resolver.</summary>
public interface IInterfaceDnsResolver
{
    ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, InterfaceBinding binding, CancellationToken cancellationToken);
}

/// <summary>HTTPS only, no system proxy/cookies, no interface fallback, including redirects and DNS.</summary>
public sealed class BoundHttpsResourceTransport(IPhysicalSocketBinder binder, IInterfaceDnsResolver resolver,
    Func<SocketsHttpHandler, HttpMessageHandler>? httpHandlerFactory = null) : IResourceTransport
{
    public async Task<byte[]> DownloadAsync(ResourceDescriptor resource, CancellationToken cancellationToken)
    {
        try { return await DownloadCoreAsync(resource, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is HttpRequestException or System.Security.Authentication.AuthenticationException or SocketException)
        {
            // SocketsHttpHandler wraps ConnectCallback failures. Recover only our sanitized facts.
            for (Exception? cause = error; cause is not null; cause = cause.InnerException)
                if (cause is ResourceTransportFailure known) throw known;
            string code = error is System.Security.Authentication.AuthenticationException ? "tls.failed" : NetworkFailure.Code(error);
            throw new ResourceTransportFailure(code == "tls.failed" ? ResourceTransportStage.Tls : ResourceTransportStage.Http, code,
                error is HttpRequestException { StatusCode: { } status } ? (int)status : null);
        }
        catch (IOException error) when (error is not ResourceTransportFailure)
        { throw new ResourceTransportFailure(ResourceTransportStage.Body, NetworkFailure.Code(error)); }
    }
    private async Task<byte[]> DownloadCoreAsync(ResourceDescriptor resource, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(resource);
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectCallback = (context, token) => ConnectAsync(context.DnsEndPoint, resource.Exit.Interface!, token)
        };
        using var client = new HttpClient(httpHandlerFactory?.Invoke(handler) ?? handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EgressController/1.0");
        Uri url = resource.Url;
        for (int redirects = 0; redirects <= 5; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url) { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (redirects == 5 || response.Headers.Location is not { } location) throw new ResourceTransportFailure(ResourceTransportStage.Http, "redirect.invalid");
                url = location.IsAbsoluteUri ? location : new Uri(url, location);
                if (!SafeHttps(url)) throw new ResourceTransportFailure(ResourceTransportStage.Http, "redirect.invalid");
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new ResourceTransportFailure(ResourceTransportStage.Http, "http.status", (int)response.StatusCode);
            if (response.Content.Headers.ContentEncoding.Count != 0) throw new ResourceTransportFailure(ResourceTransportStage.Body, "response.encoding");
            if (response.Content.Headers.ContentLength is { } length && length > resource.MaximumBytes) throw new ResourceTransportFailure(ResourceTransportStage.Body, "response.size");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var output = new MemoryStream();
            byte[] buffer = new byte[65536];
            while (true)
            {
                int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (output.Length + read > resource.MaximumBytes) throw new ResourceTransportFailure(ResourceTransportStage.Body, "response.size");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        throw new ResourceTransportFailure(ResourceTransportStage.Http, "redirect.invalid");
    }

    // Public to allow pure boundary tests to prove a failed binder prevents connect.
    public async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint, InterfaceBinding binding, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(binding.StableId) || string.IsNullOrWhiteSpace(binding.BsdName)) throw new IOException("A physical interface snapshot is required.");
        IReadOnlyList<IPAddress> addresses;
        try { addresses = IPAddress.TryParse(endpoint.Host, out var literal) ? [literal] : await resolver.ResolveAsync(endpoint.Host, binding, token).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or HttpRequestException or SocketException)
        { throw new ResourceTransportFailure(ResourceTransportStage.Dns, NetworkFailure.Code(error)); }
        if (addresses.Count == 0 || addresses.Count > 64) throw new ResourceTransportFailure(ResourceTransportStage.Dns, "dns.failed");
        Exception? failure = null;
        foreach (var address in addresses.Distinct())
        {
            token.ThrowIfCancellationRequested();
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                binder.Bind(socket, binding); // Never caught/retried on a different or unbound interface.
            }
            catch { socket.Dispose(); throw new ResourceTransportFailure(ResourceTransportStage.Bind, "interface.bind"); }
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token); attempt.CancelAfter(TimeSpan.FromSeconds(2));
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), attempt.Token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException error) { failure = error; socket.Dispose(); }
            catch (OperationCanceledException error) when (!token.IsCancellationRequested) { failure = new TimeoutException("Connection timeout.", error); socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        throw new ResourceTransportFailure(ResourceTransportStage.Tcp, failure is null ? "connection.failed" : NetworkFailure.Code(failure));
    }

    private static void Validate(ResourceDescriptor resource)
    {
        if (!SafeHttps(resource.Url) || resource.MaximumBytes < 1 || resource.Exit.Kind is not (ResourceExitKind.BoundInterface or ResourceExitKind.DirectSubscription) || !resource.Exit.IsValid)
            throw new ArgumentException("HTTPS and an explicit physical interface are required.", nameof(resource));
    }
    private static bool SafeHttps(Uri url) => url.IsAbsoluteUri && url.Scheme == Uri.UriSchemeHttps && url.UserInfo.Length == 0 && url.Fragment.Length == 0;
}
