using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using EgressController.SingBox.Api.Models;

namespace EgressController.SingBox.Api;

/// <summary>
/// The small, product-facing subset of sing-box's loopback Clash API.
/// It deliberately does not expose proxy, provider, script, or profile endpoints.
/// </summary>
public sealed class SingBoxApiClient : IDisposable
{
    public const int MaxRestResponseBytes = 8 * 1024 * 1024;
    public const int MaxWebSocketMessageBytes = 1 * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Uri _controllerUri;
    private readonly string _secret;
    private bool _disposed;

    public SingBoxApiClient(Uri controllerUri, string secret, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(controllerUri);
        if (!controllerUri.IsAbsoluteUri)
            throw new ArgumentException("The sing-box controller URI must be absolute.", nameof(controllerUri));
        if (controllerUri.Scheme is not ("http" or "https"))
            throw new ArgumentException("The sing-box controller URI must use HTTP or HTTPS.", nameof(controllerUri));
        if (!controllerUri.HostNameType.Equals(UriHostNameType.IPv4) &&
            !controllerUri.HostNameType.Equals(UriHostNameType.IPv6) &&
            !string.Equals(controllerUri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The sing-box controller must be loopback-only.", nameof(controllerUri));
        }

        IPAddress[] addresses = [];
        if (IPAddress.TryParse(controllerUri.Host, out IPAddress? address))
            addresses = [address];
        if (addresses.Length > 0 && !IPAddress.IsLoopback(addresses[0]))
            throw new ArgumentException("The sing-box controller must be loopback-only.", nameof(controllerUri));

        if (string.IsNullOrWhiteSpace(secret))
            throw new ArgumentException("The sing-box controller secret is required.", nameof(secret));
        if (secret.Length > 512)
            throw new ArgumentException("The sing-box controller secret is too long.", nameof(secret));

        _controllerUri = controllerUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? controllerUri
            : new Uri(controllerUri.AbsoluteUri + "/", UriKind.Absolute);
        _secret = secret;
        _httpClient = httpClient ?? CreateDefaultHttpClient();
        _ownsHttpClient = httpClient is null;
    }

    public Uri ControllerUri => _controllerUri;

    public Task<SingBoxVersionResponse> GetVersionAsync(CancellationToken cancellationToken = default)
        => GetJsonAsync("version", SingBoxApiJsonContext.Default.SingBoxVersionResponse, cancellationToken);

    public Task<SingBoxConfigResponse> GetConfigAsync(CancellationToken cancellationToken = default)
        => GetJsonAsync("configs", SingBoxApiJsonContext.Default.SingBoxConfigResponse, cancellationToken);

    public Task<SingBoxRulesResponse> GetRulesAsync(CancellationToken cancellationToken = default)
        => GetJsonAsync("rules", SingBoxApiJsonContext.Default.SingBoxRulesResponse, cancellationToken);

    public Task<SingBoxConnectionsResponse> GetConnectionsAsync(CancellationToken cancellationToken = default)
        => GetJsonAsync("connections", SingBoxApiJsonContext.Default.SingBoxConnectionsResponse, cancellationToken);

    public Task CloseConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("The connection id is required.", nameof(connectionId));
        return SendNoContentAsync($"connections/{Uri.EscapeDataString(connectionId)}", HttpMethod.Delete, cancellationToken);
    }

    public Task CloseAllConnectionsAsync(CancellationToken cancellationToken = default)
        => SendNoContentAsync("connections", HttpMethod.Delete, cancellationToken);

    public Task<SingBoxDnsResponse> QueryDnsAsync(
        string host,
        string recordType = "A",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("The DNS host is required.", nameof(host));
        if (string.IsNullOrWhiteSpace(recordType) || recordType.Length > 16)
            throw new ArgumentException("The DNS record type is invalid.", nameof(recordType));

        string query = $"dns/query?name={Uri.EscapeDataString(host)}&type={Uri.EscapeDataString(recordType.ToUpperInvariant())}";
        return GetJsonAsync(query, SingBoxApiJsonContext.Default.SingBoxDnsResponse, cancellationToken);
    }

    public Task FlushDnsCacheAsync(CancellationToken cancellationToken = default)
        => SendNoContentAsync("cache/dns/flush", HttpMethod.Post, cancellationToken);

    public Task FlushFakeIpCacheAsync(CancellationToken cancellationToken = default)
        => SendNoContentAsync("cache/fakeip/flush", HttpMethod.Post, cancellationToken);

    public Task<ClientWebSocket> ConnectTrafficWebSocketAsync(CancellationToken cancellationToken = default)
        => ConnectWebSocketAsync("traffic", cancellationToken);

    public Task<ClientWebSocket> ConnectConnectionsWebSocketAsync(
        int intervalMilliseconds = 1000,
        CancellationToken cancellationToken = default)
    {
        if (intervalMilliseconds is < 100 or > 60_000)
            throw new ArgumentOutOfRangeException(nameof(intervalMilliseconds), "The WebSocket interval must be between 100 and 60000 milliseconds.");
        return ConnectWebSocketAsync($"connections?interval={intervalMilliseconds}", cancellationToken);
    }

    public Task<ClientWebSocket> ConnectLogsWebSocketAsync(
        string level = "info",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(level) || level.Length > 16 ||
            level.Any(character => !char.IsLetter(character)))
        {
            throw new ArgumentException("The sing-box log level is invalid.", nameof(level));
        }
        return ConnectWebSocketAsync($"logs?level={Uri.EscapeDataString(level.ToLowerInvariant())}", cancellationToken);
    }

    public Uri CreateWebSocketUri(string pathAndQuery)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Uri httpUri = CreateHttpUri(pathAndQuery);
        var builder = new UriBuilder(httpUri)
        {
            Scheme = httpUri.Scheme == Uri.UriSchemeHttps ? Uri.UriSchemeWss : Uri.UriSchemeWs,
            Port = httpUri.Port,
        };
        return builder.Uri;
    }

    public static async Task<string?> ReceiveTextMessageAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(socket);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var payload = new MemoryStream();
            while (true)
            {
                ValueWebSocketReceiveResult result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    return null;
                if (result.MessageType != WebSocketMessageType.Text)
                    throw new SingBoxApiException("sing-box API returned a non-text WebSocket message.");

                if (payload.Length + result.Count > MaxWebSocketMessageBytes)
                    throw new SingBoxApiException("sing-box API WebSocket message exceeded the bounded size limit.");
                await payload.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken).ConfigureAwait(false);
                if (result.EndOfMessage)
                    return Encoding.UTF8.GetString(payload.GetBuffer(), 0, checked((int)payload.Length));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static SingBoxConnectionsResponse ParseConnectionsMessage(string json)
        => DeserializeWebSocketMessage(json, SingBoxApiJsonContext.Default.SingBoxConnectionsResponse);

    public static SingBoxTrafficEvent ParseTrafficMessage(string json)
        => DeserializeWebSocketMessage(json, SingBoxApiJsonContext.Default.SingBoxTrafficEvent);

    public static SingBoxLogEvent ParseLogMessage(string json)
        => DeserializeWebSocketMessage(json, SingBoxApiJsonContext.Default.SingBoxLogEvent);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    private async Task<T> GetJsonAsync<T>(
        string pathAndQuery,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, pathAndQuery);
        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        byte[] body = await ReadResponseBodyAsync(response, pathAndQuery, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize(body, typeInfo)
                ?? throw new SingBoxApiException($"sing-box API returned an empty JSON document for '{pathAndQuery}'.");
        }
        catch (JsonException exception)
        {
            throw new SingBoxApiException($"sing-box API returned invalid JSON for '{pathAndQuery}'.", exception);
        }
    }

    private async Task SendNoContentAsync(
        string pathAndQuery,
        HttpMethod method,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, pathAndQuery);
        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            byte[] body = await ReadResponseBodyAsync(response, pathAndQuery, cancellationToken, validateStatus: false).ConfigureAwait(false);
            throw CreateStatusException(response.StatusCode, pathAndQuery, body);
        }
    }

    private async Task<ClientWebSocket> ConnectWebSocketAsync(
        string pathAndQuery,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {_secret}");
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        try
        {
            await socket.ConnectAsync(CreateWebSocketUri(pathAndQuery), cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string pathAndQuery)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var request = new HttpRequestMessage(method, CreateHttpUri(pathAndQuery));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _secret);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SingBoxApiException("sing-box API request timed out.");
        }
        catch (HttpRequestException exception)
        {
            throw new SingBoxApiException("sing-box API is unavailable.", exception);
        }
    }

    private static async Task<byte[]> ReadResponseBodyAsync(
        HttpResponseMessage response,
        string pathAndQuery,
        CancellationToken cancellationToken,
        bool validateStatus = true)
    {
        if (validateStatus && !response.IsSuccessStatusCode)
        {
            byte[] errorBody = await ReadResponseBodyAsync(response, pathAndQuery, cancellationToken, validateStatus: false).ConfigureAwait(false);
            throw CreateStatusException(response.StatusCode, pathAndQuery, errorBody);
        }

        if (response.Content.Headers.ContentLength is > MaxRestResponseBytes)
            throw new SingBoxApiException($"sing-box API response for '{pathAndQuery}' exceeded the bounded size limit.");

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var body = new MemoryStream();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                if (body.Length + read > MaxRestResponseBytes)
                    throw new SingBoxApiException($"sing-box API response for '{pathAndQuery}' exceeded the bounded size limit.");
                await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            return body.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static SingBoxApiException CreateStatusException(HttpStatusCode statusCode, string pathAndQuery, byte[] body)
    {
        string detail = Encoding.UTF8.GetString(body).Trim();
        if (detail.Length > 512)
            detail = detail[..512] + "…";
        string message = statusCode == HttpStatusCode.Unauthorized
            ? "sing-box API authentication failed."
            : $"sing-box API request '{pathAndQuery}' failed with HTTP {(int)statusCode} {statusCode}.";
        return new SingBoxApiException(message, statusCode, detail);
    }

    private Uri CreateHttpUri(string pathAndQuery)
    {
        if (string.IsNullOrWhiteSpace(pathAndQuery))
            throw new ArgumentException("The API path is required.", nameof(pathAndQuery));
        return new Uri(_controllerUri, pathAndQuery.TrimStart('/'));
    }

    private static HttpClient CreateDefaultHttpClient()
        => new(new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };

    private static T DeserializeWebSocketMessage<T>(string json, JsonTypeInfo<T> typeInfo)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new SingBoxApiException("sing-box API returned an empty WebSocket message.");
        try
        {
            return JsonSerializer.Deserialize(json, typeInfo)
                ?? throw new SingBoxApiException("sing-box API returned a null WebSocket message.");
        }
        catch (JsonException exception)
        {
            throw new SingBoxApiException("sing-box API returned invalid WebSocket JSON.", exception);
        }
    }
}

public sealed class SingBoxApiException : Exception
{
    public SingBoxApiException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    public SingBoxApiException(string message, HttpStatusCode statusCode, string responseBody)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public HttpStatusCode? StatusCode { get; }

    public string? ResponseBody { get; }
}
