namespace EgressController.Transport.Upstream;

public interface IHttpDownloadTransport
{
    Task<T> ExecuteAsync<T>(Uri resource, Func<HttpClient, CancellationToken, Task<T>> transfer, CancellationToken token, TimeSpan? timeout = null);
}

public sealed class DownloadException(string code, string message, Exception? inner = null) : IOException(message, inner)
{
    public string Code { get; } = code;
}

/// <summary>One immutable default-port snapshot per transfer, including redirects and response body.</summary>
public sealed class DefaultProxyDownloadTransport(Func<int?> defaultPort, Func<int, HttpClient>? createClient = null, CancellationToken lifetime = default) : IHttpDownloadTransport
{
    private readonly AsyncLocal<PortChoice?> _scopedPort = new();
    private sealed record PortChoice(int? Port);
    public IDisposable UseDefaultPort(int? port)
    {
        var previous = _scopedPort.Value;
        _scopedPort.Value = new(port);
        Status = "正在校验本地内核与规则缓存";
        return new PortScope(() => _scopedPort.Value = previous);
    }
    private sealed class PortScope(Action restore) : IDisposable
    {
        private Action? _restore = restore;
        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }
    public event Action<string>? Activity;
    private volatile string _status = "优先使用已校验的本地缓存";
    public string Status { get => _status; private set => _status = value; }
    public async Task<T> ExecuteAsync<T>(Uri resource, Func<HttpClient, CancellationToken, Task<T>> transfer, CancellationToken token, TimeSpan? timeout = null)
    {
        token.ThrowIfCancellationRequested();
        lifetime.ThrowIfCancellationRequested();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
        token = linked.Token;
        int? port = _scopedPort.Value is { } scoped ? scoped.Port : defaultPort();
        if (port is null or < 1 or > 65535)
        {
            Status = "缺少默认 SOCKS5 端口，无法下载启动资源；请在网络与出口中设置默认端口。";
            throw new DownloadException("download.proxy.missing", Status);
        }
        // Never use the OS proxy or a direct fallback. A profile edit affects the next request;
        // it cannot dispose a client currently downloading a different artifact.
        using var client = (createClient ?? (value => Socks5HttpClientFactory.Create(value)))(port.Value);
        string route = $"默认 SOCKS5 127.0.0.1:{port}";
        Status = $"通过{route}下载 {resource.Host}{resource.AbsolutePath}";
        try { Activity?.Invoke(Status); } catch { /* Diagnostic observers have no transfer authority. */ }
        try
        {
            var result = await DownloadExecution.RunAsync(client, route, resource, transfer, token, timeout).ConfigureAwait(false);
            Status = $"下载完成 · {route} · {resource.Host}{resource.AbsolutePath}";
            return result;
        }
        catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
        {
            Status = error.Message;
            throw;
        }
    }
}

/// <summary>Supplied-client adapter for explicit transport injection (including in-memory tests).</summary>
public sealed class ClientDownloadTransport(HttpClient client) : IHttpDownloadTransport
{
    public Task<T> ExecuteAsync<T>(Uri resource, Func<HttpClient, CancellationToken, Task<T>> transfer, CancellationToken token, TimeSpan? timeout = null)
        => DownloadExecution.RunAsync(client, "指定 HTTP 传输", resource, transfer, token,
            timeout is { } requested && client.Timeout != Timeout.InfiniteTimeSpan && client.Timeout < requested ? client.Timeout : timeout);
}

internal static class DownloadExecution
{
    public static async Task<T> RunAsync<T>(HttpClient client, string route, Uri resource,
        Func<HttpClient, CancellationToken, Task<T>> transfer, CancellationToken token, TimeSpan? timeout = null)
    {
        using var deadline = HttpTransfer.Deadline(client, token, timeout);
        try { return await transfer(client, deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException error)
        {
            throw new DownloadException("download.timeout",
                $"下载超时：{resource.Host}{resource.AbsolutePath}；出口：{route}；连接或正文传输未在时限内完成。", error);
        }
        catch (HttpRequestException error)
        {
            throw new DownloadException("download.http",
                $"下载失败：{resource.Host}{resource.AbsolutePath}；出口：{route}；{error.GetBaseException().Message}", error);
        }
    }
}
