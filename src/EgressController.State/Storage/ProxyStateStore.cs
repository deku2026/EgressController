using EgressController.Core.Models;
using EgressController.State.Json;

namespace EgressController.State.Storage;

/// <summary>
/// Durable record of who owns the System Proxy (plan §1.8 / §12) so a later/after-crash start can
/// decide Restore-vs-Reclaim. Written atomically (tmp→flush→move+backup) via source-gen JSON.
/// </summary>
public sealed record ProxyStateRecord(
    Guid SessionId,
    SystemProxyState? Previous,
    SystemProxyState? Ours,
    bool Active,
    DateTimeOffset TimestampUtc);

/// <summary>Loads/saves <c>proxy-state.json</c> under a data directory.</summary>
public sealed class ProxyStateStore(string baseDir)
{
    private readonly string _path = Path.Combine(baseDir, "proxy-state.json");

    public ProxyStateRecord Load()
        => AtomicJsonFile.Read(_path, EgressStateJsonContext.Default.ProxyStateRecord,
            new ProxyStateRecord(Guid.Empty, null, null, false, DateTimeOffset.MinValue));

    public void Save(ProxyStateRecord record)
        => AtomicJsonFile.Write(_path, record, EgressStateJsonContext.Default.ProxyStateRecord);

    public bool Exists => File.Exists(_path);
}