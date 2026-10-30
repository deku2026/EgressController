using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using EgressController.Policy;

namespace EgressController.Runtime;

public enum PreparationStage { Idle, CheckingCache, Downloading, Validating, Publishing, Ready, Canceled, Failed }
public enum PreparationCachePolicy { CacheFirst, Refresh }
public sealed record PreparationProgress(long Revision, string ResourceId, PreparationStage Stage, string Message);
public sealed class ResourceDescriptor
{
    public required string Id { get; init; }
    public required Uri Url { get; init; }
    public required InterfaceBinding Interface { get; init; }
    public int MaximumBytes { get; init; } = 16 * 1024 * 1024;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public string? ExpectedSha256 { get; init; }
    public PreparationCachePolicy CachePolicy { get; init; } = PreparationCachePolicy.CacheFirst;
}
public interface IResourceTransport { Task<byte[]> DownloadAsync(ResourceDescriptor resource, CancellationToken cancellationToken); }
public interface IValidatedResourceCache
{
    Task<byte[]?> ReadAsync(string id, CancellationToken cancellationToken);
    Task WriteAsync(string id, byte[] bytes, CancellationToken cancellationToken);
}
public sealed class ResourcePreparationException(string resourceId, string interfaceId, int remotePort, PreparationStage stage, bool timeout)
    : IOException($"资源 {resourceId} 在 {stage} 阶段{(timeout ? "超时" : "失败")}；接口 {interfaceId}，远端端口 {remotePort}。")
{
    public string ResourceId { get; } = resourceId;
    public string InterfaceId { get; } = interfaceId;
    public int RemotePort { get; } = remotePort;
    public PreparationStage Stage { get; } = stage;
    public bool IsTimeout { get; } = timeout;
}

/// <summary>Each operation owns its cancellation source and immutable physical-interface snapshot.</summary>
public sealed class ResourcePreparation(IResourceTransport transport, IValidatedResourceCache cache)
{
    private readonly object _sync = new();
    private long _revision;
    private CancellationTokenSource? _active;
    private readonly Queue<ResourcePreparationException> _history = new();
    public PreparationProgress Progress { get; private set; } = new(0, "", PreparationStage.Idle, "");
    public IReadOnlyList<ResourcePreparationException> History { get { lock (_sync) return _history.ToArray(); } }

    public async Task<byte[]> PrepareAsync(ResourceDescriptor resource, Func<byte[], bool> validate,
        CancellationToken cancellationToken = default)
    {
        if (resource.Url.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(resource.Interface.StableId)
            || string.IsNullOrWhiteSpace(resource.Interface.BsdName) || resource.MaximumBytes < 1 || resource.Timeout <= TimeSpan.Zero)
            throw new ArgumentException("资源必须使用 HTTPS 和明确的物理出口接口。", nameof(resource));
        using var ownedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        long revision;
        lock (_sync)
        {
            _active?.Cancel(); // Its owning operation alone disposes it.
            _active = ownedCancellation;
            revision = ++_revision;
            Progress = new(revision, resource.Id, PreparationStage.CheckingCache, "正在检查本地缓存");
        }
        var token = ownedCancellation.Token;
        string cacheKey = CacheKey(resource);
        PreparationStage stage = PreparationStage.CheckingCache;
        try
        {
            byte[]? cached = resource.CachePolicy == PreparationCachePolicy.Refresh ? null
                : await cache.ReadAsync(cacheKey, token).ConfigureAwait(false);
            if (cached is not null && Valid(cached))
            { token.ThrowIfCancellationRequested(); Publish(PreparationStage.Ready, "本地缓存可用"); return cached; }
            stage = PreparationStage.Downloading;
            Publish(stage, "正在通过选定出口获取资源");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(resource.Timeout);
            byte[] downloaded;
            try { downloaded = await transport.DownloadAsync(resource, timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new ResourcePreparationException(resource.Id, resource.Interface.StableId, resource.Url.Port, stage, timeout: true); }
            stage = PreparationStage.Validating;
            Publish(stage, "正在验证资源");
            if (!Valid(downloaded)) throw new ResourcePreparationException(resource.Id, resource.Interface.StableId, resource.Url.Port, stage, timeout: false);
            stage = PreparationStage.Publishing;
            Publish(stage, "正在保存已验证缓存");
            token.ThrowIfCancellationRequested();
            await cache.WriteAsync(cacheKey, downloaded, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Publish(PreparationStage.Ready, "资源已就绪");
            return downloaded;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { Publish(PreparationStage.Canceled, "准备已取消"); throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var failure = exception as ResourcePreparationException
                ?? new ResourcePreparationException(resource.Id, resource.Interface.StableId, resource.Url.Port, stage, timeout: false);
            lock (_sync)
            {
                _history.Enqueue(failure);
                while (_history.Count > 30) _history.Dequeue();
            }
            Publish(PreparationStage.Failed, failure.Message);
            throw failure;
        }
        finally
        {
            lock (_sync) { if (ReferenceEquals(_active, ownedCancellation)) _active = null; }
        }

        bool Valid(byte[] bytes) => bytes.Length is > 0 && bytes.Length <= resource.MaximumBytes
            && (resource.ExpectedSha256 is null || string.Equals(resource.ExpectedSha256, Convert.ToHexStringLower(SHA256.HashData(bytes)), StringComparison.OrdinalIgnoreCase))
            && validate(bytes);
        void Publish(PreparationStage next, string message)
        {
            lock (_sync) { if (_revision == revision) Progress = new(revision, resource.Id, next, message); }
        }
    }

    public static string CacheKey(ResourceDescriptor resource) => resource.Id + "."
        + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(resource.Url.AbsoluteUri)));
}

public sealed class InMemoryResourceCache : IValidatedResourceCache
{
    private readonly ConcurrentDictionary<string, byte[]> _values = new(StringComparer.Ordinal);
    public Task<byte[]?> ReadAsync(string id, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(_values.TryGetValue(id, out var bytes) ? bytes.ToArray() : null); }
    public Task WriteAsync(string id, byte[] bytes, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); _values[id] = bytes.ToArray(); return Task.CompletedTask; }
}
public sealed class FixtureResourceTransport : IResourceTransport
{
    private readonly Dictionary<string, byte[]> _resources = new(StringComparer.Ordinal);
    public int RequestCount { get; private set; }
    public List<InterfaceBinding> UsedInterfaces { get; } = [];
    public void Add(Uri uri, byte[] bytes) => _resources[uri.AbsoluteUri] = bytes.ToArray();
    public Task<byte[]> DownloadAsync(ResourceDescriptor resource, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); RequestCount++; UsedInterfaces.Add(resource.Interface);
        if (!_resources.TryGetValue(resource.Url.AbsoluteUri, out var bytes)) throw new IOException("Mock resource not found.");
        return Task.FromResult(bytes.ToArray());
    }
}
