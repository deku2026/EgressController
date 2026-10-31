using EgressController.Transport.Upstream;

namespace EgressController.Runtime;

/// <summary>Every request owns its default-port snapshot; redirects retain that same Xray SOCKS exit.</summary>
public sealed class XrayResourceTransport(Func<int, CancellationToken, Task> requireReady,
    Func<int, HttpClient>? clientFactory = null) : IResourceTransport
{
    public async Task<byte[]> DownloadAsync(ResourceDescriptor resource, CancellationToken cancellationToken)
    {
        if (resource.Exit.Kind != ResourceExitKind.XrayProxy || !resource.Exit.IsValid || !SafeUrl(resource.Url))
            throw new ArgumentException("An explicit Xray SOCKS port and HTTPS resource are required.");
        await requireReady(resource.Exit.ProxyPort, cancellationToken).ConfigureAwait(false);
        var transport = new DefaultProxyDownloadTransport(() => resource.Exit.ProxyPort,
            clientFactory ?? (port => Socks5HttpClientFactory.Create(port, allowAutoRedirect: false)));
        return await transport.ExecuteAsync(resource.Url, async (client, token) =>
        {
            Uri url = resource.Url;
            for (int redirects = 0; redirects <= 5; redirects++)
            {
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    if (redirects == 5 || response.Headers.Location is not { } location) throw new IOException("Resource redirect limit.");
                    url = location.IsAbsoluteUri ? location : new Uri(url, location);
                    if (!SafeUrl(url)) throw new IOException("Resource redirects must remain HTTPS without credentials.");
                    continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is { } length && length > resource.MaximumBytes)
                    throw new IOException("Resource size limit exceeded.");
                await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var output = new MemoryStream();
                await HttpTransfer.CopyBoundedAsync(input, output, resource.MaximumBytes, token).ConfigureAwait(false);
                return output.ToArray();
            }
            throw new IOException("Resource redirect limit.");
        }, cancellationToken, resource.Timeout).ConfigureAwait(false);
    }
    private static bool SafeUrl(Uri url) => url.IsAbsoluteUri && url.Scheme == "https" && url.UserInfo.Length == 0 && url.Fragment.Length == 0;
}
