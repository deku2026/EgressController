// SPDX-License-Identifier: MIT
namespace EgressController.Mac;

public interface IMacIconSource
{
    byte[] ReadPng(string canonicalBundlePath, int pixelSize);
}

/// <summary>Bounded LRU, shared in-flight reads, and at most four background native icon reads.</summary>
public sealed class MacIconCache(IMacIconSource source, int capacity = 256)
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _reads = new(4, 4);
    private readonly Dictionary<(string Path, int Size), (Task<byte[]> Task, LinkedListNode<(string Path, int Size)> Node)> _cache = [];
    private readonly LinkedList<(string Path, int Size)> _order = [];

    public async Task<byte[]> GetPngAsync(string canonicalBundlePath, int pixelSize = 64, CancellationToken cancellationToken = default)
    {
        if (!MacApplicationCatalog.IsApplicationRoot(canonicalBundlePath)) throw new ArgumentException("Expected a canonical .app path.", nameof(canonicalBundlePath));
        if (pixelSize is < 16 or > 512) throw new ArgumentOutOfRangeException(nameof(pixelSize));
        if (capacity < 1) throw new InvalidOperationException("Icon cache capacity must be positive.");
        Task<byte[]> task;
        var key = (canonicalBundlePath, pixelSize);
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var item))
            { _order.Remove(item.Node); _order.AddLast(item.Node); task = item.Task; }
            else
            {
                while (_cache.Count >= capacity && _order.First is { } first) { _cache.Remove(first.Value); _order.RemoveFirst(); }
                task = ReadAsync(key);
                _cache.Add(key, (task, _order.AddLast(key)));
            }
        }
        try { return (await task.WaitAsync(cancellationToken).ConfigureAwait(false)).ToArray(); }
        catch when (task.IsFaulted)
        {
            lock (_gate)
                if (_cache.TryGetValue(key, out var item) && ReferenceEquals(item.Task, task)) { _cache.Remove(key); _order.Remove(item.Node); }
            throw;
        }
    }
    private async Task<byte[]> ReadAsync((string Path, int Size) key)
    {
        await _reads.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(() => source.ReadPng(key.Path, key.Size)).ConfigureAwait(false); }
        finally { _reads.Release(); }
    }
}
